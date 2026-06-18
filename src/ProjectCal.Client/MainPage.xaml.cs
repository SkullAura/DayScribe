using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ProjectCal.Shared;
using ProjectCal_Client.Services;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace ProjectCal_Client;

public sealed partial class MainPage : Page
{
    private const string ThemeSettingKey = "settings_theme";
    private const string AppLanguageSettingKey = "settings_app_language";
    private const string DefaultDurationSettingKey = "settings_default_duration";
    private const string TranscriptionLanguageSettingKey = "settings_transcription_language";
    private const string AutoSyncMediaSettingKey = "settings_auto_sync_media";
    private const string LocalApiUrl = "http://localhost:5009";
    private const string ManagedCloudApiUrl = "https://notesmuchachos.onrender.com";
    private const string UpdateBranch = "main";
    private const string UpdateCommitUrl = "https://api.github.com/repos/SkullAura/NotesMuchachos/commits/" + UpdateBranch;
    private const string UpdateLatestReleaseUrl = "https://api.github.com/repos/SkullAura/NotesMuchachos/releases/latest";
    private const string UpdateInstallerAssetName = "NotesMuchachosSetup.exe";

    private readonly LocalNoteStore _store = new();
    private readonly ProjectCalApiClient _api = new();
    private static readonly HttpClient UpdateHttpClient = new();
    private DateTimeOffset? _lastSyncAt;
    private Guid? _selectedNoteId;
    private Guid? _resizeHandlesNoteId;
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Now);
    private MediaCapture? _mediaCapture;
    private MediaPlayer? _audioPlayer;
    private StorageFile? _recordingFile;
    private LocalAttachment? _selectedAudioAttachment;
    private Guid? _loadedAudioAttachmentId;
    private Guid? _expandedAudioAttachmentId;
    private LocalNote? _resizingNote;
    private FrameworkElement? _resizeVisual;
    private ResizeEdge _resizeEdge;
    private int _resizeOriginalStartMinutes;
    private int _resizeOriginalEndMinutes;
    private double _resizeStartY;
    private bool _isRecording;
    private bool _isUpdatingDateSelector;

    private const double HourHeight = 118;
    private const double MinuteHeight = HourHeight / 60.0;

    private enum ResizeEdge
    {
        Top,
        Bottom
    }

    private enum AudioListCommand
    {
        Play,
        Pause,
        Stop
    }

    private sealed record ResizeContext(LocalNote Note, FrameworkElement Host, ResizeEdge Edge);
    private sealed record TimelineLayout(LocalNote Note, LocalAttachmentSummary? AttachmentSummary, int Lane, int LaneCount);
    private sealed record StoredRichBody(string Format, string Plain, string Rtf);
    private sealed record UpdateCheckResult(bool Success, bool UpdateAvailable, string Message, string? LocalSha, string? RemoteSha, string? InstallerDownloadUrl = null);
    private sealed record AudioTranscriptSelection(LocalAttachment Attachment, LocalTranscript? Transcript);
    private sealed record AudioControlContext(LocalAttachment Attachment, LocalTranscript? Transcript, AudioListCommand Command);

    public MainPage()
    {
        InitializeComponent();
        ApplyThemeSetting(GetStringSetting(ThemeSettingKey, "Light"));
        ApplyLanguageSetting(GetStringSetting(AppLanguageSettingKey, "en"));
        ConfigureApiClientFromSettings(resetSession: false);
        _audioPlayer = CreateAudioPlayer();
        AudioPlayerElement.SetMediaPlayer(_audioPlayer);
        SetSelectedDate(DateOnly.FromDateTime(DateTime.Now), reload: false);
        StartTimePicker.Time = TimeSpan.FromHours(DateTimeOffset.Now.Hour);
        EndTimePicker.Time = TimeOnly.FromTimeSpan(StartTimePicker.Time).AddMinutes(GetDefaultDurationMinutes()).ToTimeSpan();
        Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await _store.InitializeAsync();
        AuthStatusBox.Text = T("checkingSession");
        if (await _api.TryRestoreSessionAsync())
        {
            OpenAppShell(T("welcomeBack"));
            await ReloadAsync();
            return;
        }

        AuthStatusBox.Text = T("registerOnce");
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            var result = await _api.RegisterAsync(EmailBox.Text, PasswordBox.Password);
            TokenBox.Text = result.DevelopmentEmailToken ?? "";
            AuthStatusBox.Text = T("accountCreated");
        });
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            await _api.LoginAsync(EmailBox.Text, PasswordBox.Password);
            OpenAppShell(T("signedInRemembered"));
            await ReloadAsync();
        });
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _api.Logout();
        ReturnToAuthScreen(T("loggedOut"));
        await ReloadAsync();
    }

    private async void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            TokenBox.Text = await _api.ForgotPasswordAsync(EmailBox.Text) ?? "";
            AuthStatusBox.Text = T("resetTokenCreated");
        });
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            await _api.ResetPasswordAsync(EmailBox.Text, TokenBox.Text, PasswordBox.Password);
            AuthStatusBox.Text = T("passwordChanged");
        });
    }

    private async void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await SaveCurrentNoteDraftAsync(updateStatus: true, force: true);
            await ReloadAsync();
        });
    }

    private async void NewNote_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentNoteDraftAsync();
        StartNewNote(TimeOnly.FromTimeSpan(StartTimePicker.Time));
        await ReloadAsync();
    }

    private async void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNoteId is null)
        {
            StatusBox.Text = "Select a note first.";
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete note",
            Content = "This note will be removed from the timeline.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await _store.DeleteNoteAsync(_selectedNoteId.Value);
            _selectedNoteId = null;
            _resizeHandlesNoteId = null;
            TitleBox.Text = "";
            SetBodyContent("");
            ClearMediaState();
            SetTranscriptState((LocalNote?)null);
            StatusBox.Text = "Deleted locally. Sync will remove it from the server.";
            SyncStateText.Text = "Local changes";
            await ReloadAsync();
        });
    }

    private async void AttachPhoto_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await EnsureCurrentNoteAsync("Photo note");
            var noteId = _selectedNoteId!.Value;
            await _store.AddAttachmentAsync(noteId, file, AttachmentType.Photo);
            await LoadSelectedMediaAsync(noteId);
            StatusBox.Text = "Photo attached locally.";
            SyncStateText.Text = "Local changes";
            SetMediaAction("Photo attached locally.", false);
            if (_api.IsSignedIn && GetBoolSetting(AutoSyncMediaSettingKey, false))
            {
                await SyncNowAsync();
            }

            await ReloadAsync();
        });
    }

    private async void RecordAudio_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            if (!_isRecording)
            {
                await EnsureCurrentNoteAsync("Voice note");

                _mediaCapture = new MediaCapture();
                await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio
                });

                var folder = await StorageFolder.GetFolderFromPathAsync(ClientAppData.RecordingsPath);
                _recordingFile = await folder.CreateFileAsync($"{Guid.NewGuid():N}.wav", CreationCollisionOption.GenerateUniqueName);
                await _mediaCapture.StartRecordToStorageFileAsync(MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto), _recordingFile);
                _isRecording = true;
                SetRecordButtonContent(Symbol.Stop, T("stopRecording"));
                StatusBox.Text = T("recordingStarted");
                MediaStateText.Text = "Recording";
                SetMediaAction("Recording audio...", true);
                await ReloadAsync();
                return;
            }

            await _mediaCapture!.StopRecordAsync();
            _mediaCapture.Dispose();
            _mediaCapture = null;
            _isRecording = false;
            SetRecordButtonContent(Symbol.Microphone, T("recordAudio"));

            var recordedFile = _recordingFile;
            _recordingFile = null;
            if (recordedFile is not null)
            {
                var attachment = await _store.AddAttachmentAsync(_selectedNoteId!.Value, recordedFile, AttachmentType.Audio);
                await _store.UpdateTranscriptAsync(_selectedNoteId.Value, attachment.Id, null, TranscriptStatus.Pending);
                await LoadSelectedMediaAsync(_selectedNoteId.Value);
            }

            StatusBox.Text = _api.IsSignedIn
                ? "Audio saved. Uploading for server transcription..."
                : "Audio saved locally. Sign in and sync to transcribe it.";
            SyncStateText.Text = "Local changes";
            TranscriptStateText.Text = _api.IsSignedIn ? T("queued") : T("noAudio");
            SetMediaAction(_api.IsSignedIn ? "Uploading audio for server transcription..." : "Audio is saved locally. Sign in and sync to transcribe.", _api.IsSignedIn);
            if (_api.IsSignedIn)
            {
                await SyncNowAsync();
            }

            await ReloadAsync();
        });
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (!_api.IsSignedIn)
        {
            StatusBox.Text = "Login before sync.";
            return;
        }

        await RunUiAsync(SyncNowAsync);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var appLanguageBox = SettingComboBox(T("appLanguage"), [
            ("English", "en"),
            ("Русский", "ru")
        ], NormalizeAppLanguage(GetStringSetting(AppLanguageSettingKey, "en")));

        var themeBox = SettingComboBox(T("theme"), [
            (T("themeLight"), "Light"),
            (T("themeDark"), "Dark")
        ], GetStringSetting(ThemeSettingKey, "Light"));

        var durationBox = SettingComboBox(T("defaultNoteLength"), [
            (T("duration30"), "30"),
            (T("duration60"), "60"),
            (T("duration90"), "90"),
            (T("duration120"), "120")
        ], GetDefaultDurationMinutes().ToString());

        var transcriptionLanguageBox = SettingComboBox(T("transcriptionLanguage"), [
            (T("languageAuto"), "auto"),
            (T("languageRussian"), "ru"),
            (T("languageUkrainian"), "uk"),
            (T("languageEnglish"), "en")
        ], GetStringSetting(TranscriptionLanguageSettingKey, "auto"));

        var autoSyncSwitch = new ToggleSwitch
        {
            Header = T("autoSyncMedia"),
            IsOn = GetBoolSetting(AutoSyncMediaSettingKey, false)
        };

        var updateStatusText = new TextBlock
        {
            Foreground = (Brush)Resources["MutedTextBrush"],
            Text = T("updateNotChecked"),
            TextWrapping = TextWrapping.Wrap
        };

        var checkUpdatesButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new SymbolIcon(Symbol.Sync),
                    new TextBlock { Text = T("checkUpdates") }
                }
            }
        };

        var installUpdateButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new SymbolIcon(Symbol.Download),
                    new TextBlock { Text = T("installUpdate") }
                }
            }
        };

        UpdateCheckResult? latestUpdateResult = null;
        checkUpdatesButton.Click += async (_, _) =>
        {
            checkUpdatesButton.IsEnabled = false;
            installUpdateButton.IsEnabled = false;
            updateStatusText.Text = T("checkingUpdates");
            var result = await CheckForUpdatesAsync();
            latestUpdateResult = result;
            updateStatusText.Text = result.Message;
            checkUpdatesButton.IsEnabled = true;
            installUpdateButton.IsEnabled = result.Success && result.UpdateAvailable;
        };
        installUpdateButton.Click += async (_, _) =>
        {
            checkUpdatesButton.IsEnabled = false;
            installUpdateButton.IsEnabled = false;
            updateStatusText.Text = T("downloadingUpdate");
            updateStatusText.Text = await DownloadAndLaunchUpdateInstallerAsync(latestUpdateResult);
            checkUpdatesButton.IsEnabled = true;
        };

        var updatePanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = T("updates"),
                    Foreground = (Brush)Resources["InkBrush"],
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                checkUpdatesButton,
                installUpdateButton,
                updateStatusText
            }
        };

        var panel = new StackPanel
        {
            Spacing = 14,
            MinWidth = 360,
            Children =
            {
                appLanguageBox,
                themeBox,
                durationBox,
                transcriptionLanguageBox,
                autoSyncSwitch,
                updatePanel
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("settings"),
            Content = panel,
            PrimaryButtonText = T("save"),
            CloseButtonText = T("cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var appLanguage = NormalizeAppLanguage(SelectedTag(appLanguageBox, "en"));
        var theme = SelectedTag(themeBox, "Light");
        var duration = SelectedTag(durationBox, "60");
        var transcriptionLanguage = SelectedTag(transcriptionLanguageBox, "auto");

        SetSetting(AppLanguageSettingKey, appLanguage);
        SetSetting(ThemeSettingKey, theme);
        SetSetting(DefaultDurationSettingKey, duration);
        SetSetting(TranscriptionLanguageSettingKey, transcriptionLanguage);
        SetSetting(AutoSyncMediaSettingKey, autoSyncSwitch.IsOn);

        ApplyThemeSetting(theme);
        ApplyLanguageSetting(appLanguage);

        if (_selectedNoteId is null)
        {
            EndTimePicker.Time = TimeOnly.FromTimeSpan(StartTimePicker.Time).AddMinutes(GetDefaultDurationMinutes()).ToTimeSpan();
        }

        StatusBox.Text = T("settingsSaved");
    }

    private async Task SyncNowAsync()
    {
        await SaveCurrentNoteDraftAsync();
        SyncStateText.Text = "Syncing";
        SetMediaAction("Syncing notes and media...", true);
        var dirtyNotes = await _store.GetDirtyNotesAsync();
        var mutations = dirtyNotes.Select(note => new SyncNoteMutation(
            note.DeletedAt is null ? SyncOperation.Upsert : SyncOperation.Delete,
            ToUpsertNoteRequest(note))).ToArray();

        var response = await _api.SyncAsync(new SyncRequest(_lastSyncAt, mutations));
        foreach (var note in response.Notes)
        {
            await _store.ApplyServerNoteAsync(note);
        }

        foreach (var transcript in response.Transcripts)
        {
            await _store.ApplyServerTranscriptAsync(transcript);
        }

        var downloadedMedia = 0;
        var skippedMedia = 0;
        foreach (var attachment in response.Attachments)
        {
            try
            {
                var content = await _api.DownloadAttachmentAsync(attachment.Id);
                if (await _store.ApplyServerAttachmentAsync(attachment, content))
                {
                    downloadedMedia++;
                }
            }
            catch (HttpRequestException)
            {
                skippedMedia++;
            }
        }

        var uploadedMedia = 0;
        foreach (var attachment in await _store.GetPendingAttachmentsAsync())
        {
            if (!await EnsureServerNoteForAttachmentAsync(attachment.NoteId))
            {
                skippedMedia++;
                continue;
            }

            try
            {
                await _api.UploadAttachmentAsync(attachment, GetSelectedLanguage());
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                try
                {
                    await EnsureServerNoteForAttachmentAsync(attachment.NoteId);
                    await _api.UploadAttachmentAsync(attachment, GetSelectedLanguage());
                }
                catch (HttpRequestException retryEx)
                {
                    skippedMedia++;
                    if (attachment.Type == AttachmentType.Audio)
                    {
                        await _store.UpdateTranscriptAsync(attachment.NoteId, attachment.Id, retryEx.Message, TranscriptStatus.Failed);
                    }

                    continue;
                }
            }
            catch (HttpRequestException ex)
            {
                skippedMedia++;
                if (attachment.Type == AttachmentType.Audio)
                {
                    await _store.UpdateTranscriptAsync(attachment.NoteId, attachment.Id, ex.Message, TranscriptStatus.Failed);
                }

                continue;
            }

            await _store.MarkAttachmentUploadedAsync(attachment.Id);
            uploadedMedia++;
        }

        _lastSyncAt = response.ServerTime;
        if (uploadedMedia > 0)
        {
            StatusBox.Text = $"Uploaded {uploadedMedia} media file(s). Waiting for server transcription...";
            MediaStateText.Text = "Uploaded";
            TranscriptStateText.Text = "Processing";
            SetMediaAction($"Uploaded {uploadedMedia} media file(s). Checking updates...", true);
            await PullTranscriptionUpdatesAsync();
            StatusBox.Text = (downloadedMedia, skippedMedia) switch
            {
                ( > 0, > 0) => $"Synced. Uploaded {uploadedMedia}, downloaded {downloadedMedia}, skipped {skippedMedia} media file(s).",
                ( > 0, _) => $"Synced. Uploaded {uploadedMedia} and downloaded {downloadedMedia} media file(s).",
                (_, > 0) => $"Synced. Uploaded {uploadedMedia} media file(s), skipped {skippedMedia} stale file(s).",
                _ => "Synced. Uploaded media and checked transcription."
            };
            SyncStateText.Text = "Done";
            SetMediaAction("Media synced.", false);
        }
        else
        {
            StatusBox.Text = (downloadedMedia, skippedMedia) switch
            {
                ( > 0, > 0) => $"Synced {response.Notes.Count} notes. Downloaded {downloadedMedia} media file(s), skipped {skippedMedia}.",
                ( > 0, _) => $"Synced {response.Notes.Count} notes. Downloaded {downloadedMedia} media file(s).",
                (_, > 0) => $"Synced {response.Notes.Count} notes. Skipped {skippedMedia} stale media file(s).",
                _ => $"Synced {response.Notes.Count} notes. Checked transcription updates."
            };
            SyncStateText.Text = "Done";
            SetMediaAction(downloadedMedia > 0 ? "Downloaded media from your account." : "Everything is synced.", false);
        }

        await RefreshSelectedNoteDetailsAsync();
        if (_selectedNoteId is Guid selectedNoteId)
        {
            await LoadSelectedMediaAsync(selectedNoteId);
        }

        await ReloadAsync();
    }

    private async Task<bool> EnsureServerNoteForAttachmentAsync(Guid noteId)
    {
        var note = await _store.GetNoteByIdAsync(noteId);
        if (note is null || note.DeletedAt is not null)
        {
            return false;
        }

        var mutation = new SyncNoteMutation(
            SyncOperation.Upsert,
            ToUpsertNoteRequest(note));

        var response = await _api.SyncAsync(new SyncRequest(_lastSyncAt, [mutation]));
        foreach (var serverNote in response.Notes)
        {
            await _store.ApplyServerNoteAsync(serverNote);
        }

        foreach (var transcript in response.Transcripts)
        {
            await _store.ApplyServerTranscriptAsync(transcript);
        }

        _lastSyncAt = response.ServerTime;
        return true;
    }

    private UpsertNoteRequest ToUpsertNoteRequest(LocalNote note)
    {
        return new UpsertNoteRequest(
            note.Id,
            note.Title,
            note.Body,
            note.Date,
            note.StartTime,
            note.EndTime,
            note.SyncVersion,
            null,
            null,
            null);
    }

    private async Task PullTranscriptionUpdatesAsync()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var response = await _api.SyncAsync(new SyncRequest(_lastSyncAt, []));
            foreach (var note in response.Notes)
            {
                await _store.ApplyServerNoteAsync(note);
            }

            foreach (var transcript in response.Transcripts)
            {
                await _store.ApplyServerTranscriptAsync(transcript);
            }

            _lastSyncAt = response.ServerTime;
            if (response.Transcripts.Any(x => x.Status is TranscriptStatus.Done or TranscriptStatus.Failed))
            {
                return;
            }
        }
    }

    private async void DateSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingDateSelector
            || MonthBox?.SelectedItem is null
            || DayBox?.SelectedItem is null
            || YearBox?.SelectedItem is null)
        {
            return;
        }

        try
        {
            await SaveCurrentNoteDraftAsync(dateOverride: _selectedDate);
            var year = SelectedInt(YearBox, _selectedDate.Year);
            var month = SelectedInt(MonthBox, _selectedDate.Month);
            var maxDay = DateTime.DaysInMonth(year, month);
            var day = Math.Clamp(SelectedInt(DayBox, _selectedDate.Day), 1, maxDay);

            if (ReferenceEquals(sender, MonthBox) || ReferenceEquals(sender, YearBox))
            {
                _isUpdatingDateSelector = true;
                try
                {
                    PopulateDayBox(year, month, day);
                }
                finally
                {
                    _isUpdatingDateSelector = false;
                }
            }

            _selectedDate = new DateOnly(year, month, day);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusBox.Text = $"{T("dateChangeFailed")} {ex.Message}";
        }
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (HoursPanel is null)
        {
            return;
        }

        var notes = await _store.GetNotesAsync(SelectedDate(), SearchBox?.Text);
        var attachmentSummaries = await _store.GetAttachmentSummariesForNotesAsync(notes.Select(x => x.Id));

        HoursPanel.Children.Clear();
        HoursPanel.Children.Add(BuildDayTimeline(notes, attachmentSummaries));
        await RefreshAllNotesPanelAsync();
    }

    private async Task RefreshAllNotesPanelAsync()
    {
        if (AllNotesPanel is null)
        {
            return;
        }

        var notes = await _store.GetAllNotesAsync();
        var attachmentSummaries = await _store.GetAttachmentSummariesForNotesAsync(notes.Select(x => x.Id));
        AllNotesPanel.Children.Clear();

        if (notes.Count == 0)
        {
            AllNotesPanel.Children.Add(new TextBlock
            {
                Text = T("noNotesYet"),
                Foreground = (Brush)Resources["MutedTextBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        DateOnly? currentDate = null;
        foreach (var note in notes)
        {
            if (currentDate != note.Date)
            {
                currentDate = note.Date;
                AllNotesPanel.Children.Add(new TextBlock
                {
                    Text = note.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Foreground = (Brush)Resources["MutedTextBrush"],
                    FontSize = 12,
                    Margin = new Thickness(2, currentDate == notes[0].Date ? 0 : 10, 0, 2)
                });
            }

            attachmentSummaries.TryGetValue(note.Id, out var attachmentSummary);
            AllNotesPanel.Children.Add(BuildAllNoteButton(note, attachmentSummary));
        }
    }

    private Button BuildAllNoteButton(LocalNote note, LocalAttachmentSummary? attachmentSummary)
    {
        var isSelected = _selectedNoteId == note.Id;
        var title = string.IsNullOrWhiteSpace(note.Title) ? T("untitledNote") : note.Title;
        var bodyPreview = PlainTextFromStoredBody(note.Body);
        var preview = !string.IsNullOrWhiteSpace(bodyPreview)
            ? bodyPreview
            : !string.IsNullOrWhiteSpace(note.TranscriptText)
                ? note.TranscriptText
                : T("noTextYet");

        var content = new Grid
        {
            ColumnSpacing = 8
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        content.Children.Add(new Border
        {
            Background = (Brush)Resources[isSelected ? "AccentBrush" : "AccentSoftBrush"],
            CornerRadius = new CornerRadius(3)
        });

        var textStack = new StackPanel
        {
            Spacing = 3
        };
        Grid.SetColumn(textStack, 1);
        content.Children.Add(textStack);

        textStack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = (Brush)Resources["InkBrush"],
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        textStack.Children.Add(new TextBlock
        {
            Text = $"{note.StartTime:HH:mm}  {preview}",
            Foreground = (Brush)Resources["MutedTextBrush"],
            FontSize = 12,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap
        });

        var mediaText = AllNoteMediaText(attachmentSummary);
        if (!string.IsNullOrWhiteSpace(mediaText))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = mediaText,
                Foreground = (Brush)Resources["MutedTextBrush"],
                FontSize = 11,
                Opacity = 0.8,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var button = new Button
        {
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)Resources[isSelected ? "AccentSoftBrush" : "PanelAltBrush"],
            BorderBrush = (Brush)Resources[isSelected ? "AccentBrush" : "LineBrush"],
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            Tag = note,
            Content = content
        };
        button.Click += AllNoteButton_Click;
        return button;
    }

    private string AllNoteMediaText(LocalAttachmentSummary? attachmentSummary)
    {
        if (attachmentSummary is null)
        {
            return "";
        }

        var parts = new List<string>();
        if (attachmentSummary.PhotoCount > 0)
        {
            parts.Add($"{attachmentSummary.PhotoCount} {T("photo")}");
        }

        if (attachmentSummary.AudioCount > 0)
        {
            parts.Add($"{attachmentSummary.AudioCount} {T("audio")}");
        }

        return string.Join(" / ", parts);
    }

    private async void AllNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LocalNote note })
        {
            return;
        }

        await SaveCurrentNoteDraftAsync();
        SetSelectedDate(note.Date, reload: true);
        SelectNote(note, $"{T("openedNote")} {note.Title}.");
        await LoadSelectedMediaAsync(note.Id);
        await ReloadAsync();
    }

    private FrameworkElement BuildDayTimeline(
        IReadOnlyList<LocalNote> notes,
        IReadOnlyDictionary<Guid, LocalAttachmentSummary> attachmentSummaries)
    {
        var lineBrush = (Brush)Resources["LineBrush"];
        var mutedTextBrush = (Brush)Resources["MutedTextBrush"];
        var panelBrush = (Brush)Resources["PanelBrush"];
        var panelAltBrush = (Brush)Resources["PanelAltBrush"];
        var inkBrush = (Brush)Resources["InkBrush"];
        var accentBrush = (Brush)Resources["AccentBrush"];
        var accentSoftBrush = (Brush)Resources["AccentSoftBrush"];
        var totalHeight = HourHeight * 24;

        var root = new Grid
        {
            MinHeight = totalHeight
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelCanvas = new Canvas
        {
            Height = totalHeight
        };
        root.Children.Add(labelCanvas);

        var contentLayer = new Grid
        {
            Height = totalHeight,
            Background = panelBrush
        };
        Grid.SetColumn(contentLayer, 1);
        root.Children.Add(contentLayer);

        var slotGrid = new Grid
        {
            Height = totalHeight
        };
        contentLayer.Children.Add(slotGrid);

        for (var hour = 0; hour < 24; hour++)
        {
            slotGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HourHeight) });

            var label = new TextBlock
            {
                Text = $"{hour:00}",
                Foreground = mutedTextBrush,
                FontSize = 13
            };
            Canvas.SetTop(label, hour * HourHeight + 4);
            Canvas.SetLeft(label, 8);
            labelCanvas.Children.Add(label);

            var slot = new Button
            {
                Padding = new Thickness(0),
                Background = panelBrush,
                BorderBrush = lineBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = hour,
                Content = new Border()
            };
            slot.Click += EmptyHour_Click;
            slot.DoubleTapped += EmptyHour_DoubleTapped;
            Grid.SetRow(slot, hour);
            slotGrid.Children.Add(slot);
        }

        var notesCanvas = new Canvas
        {
            Height = totalHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        notesCanvas.SizeChanged += (_, args) =>
        {
            foreach (var child in notesCanvas.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is TimelineLayout layout)
                {
                    ApplyTimelineNoteBounds(child, layout, args.NewSize.Width);
                }
            }
        };
        contentLayer.Children.Add(notesCanvas);

        foreach (var layout in BuildTimelineLayouts(notes, attachmentSummaries))
        {
            var noteVisual = BuildTimelineNote(layout.Note, layout.AttachmentSummary, panelAltBrush, inkBrush, mutedTextBrush, accentBrush, accentSoftBrush, lineBrush);
            noteVisual.Tag = layout;
            ApplyTimelineNoteBounds(noteVisual, layout, notesCanvas.ActualWidth);
            Canvas.SetTop(noteVisual, NoteStartMinute(layout.Note) * MinuteHeight);
            notesCanvas.Children.Add(noteVisual);
        }

        return root;
    }

    private IReadOnlyList<TimelineLayout> BuildTimelineLayouts(
        IReadOnlyList<LocalNote> notes,
        IReadOnlyDictionary<Guid, LocalAttachmentSummary> attachmentSummaries)
    {
        var sorted = notes
            .OrderBy(NoteStartMinute)
            .ThenBy(NoteEndMinute)
            .ToArray();
        var layouts = new List<TimelineLayout>();
        var cluster = new List<LocalNote>();
        var clusterEnd = -1;

        foreach (var note in sorted)
        {
            var start = NoteStartMinute(note);
            var end = NoteEndMinute(note);
            if (cluster.Count > 0 && start >= clusterEnd)
            {
                AddClusterLayouts(cluster, layouts, attachmentSummaries);
                cluster.Clear();
            }

            cluster.Add(note);
            clusterEnd = Math.Max(clusterEnd, end);
        }

        AddClusterLayouts(cluster, layouts, attachmentSummaries);
        return layouts;
    }

    private void AddClusterLayouts(
        IReadOnlyList<LocalNote> cluster,
        List<TimelineLayout> layouts,
        IReadOnlyDictionary<Guid, LocalAttachmentSummary> attachmentSummaries)
    {
        if (cluster.Count == 0)
        {
            return;
        }

        var laneEnds = new List<int>();
        var laneAssignments = new List<(LocalNote Note, int Lane)>();
        foreach (var note in cluster.OrderBy(NoteStartMinute).ThenBy(NoteEndMinute))
        {
            var start = NoteStartMinute(note);
            var end = NoteEndMinute(note);
            var lane = laneEnds.FindIndex(x => x <= start);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(end);
            }
            else
            {
                laneEnds[lane] = end;
            }

            laneAssignments.Add((note, lane));
        }

        var laneCount = Math.Max(1, laneEnds.Count);
        foreach (var assignment in laneAssignments)
        {
            attachmentSummaries.TryGetValue(assignment.Note.Id, out var attachmentSummary);
            layouts.Add(new TimelineLayout(assignment.Note, attachmentSummary, assignment.Lane, laneCount));
        }
    }

    private static void ApplyTimelineNoteBounds(FrameworkElement noteVisual, TimelineLayout layout, double canvasWidth)
    {
        var availableWidth = Math.Max(120, canvasWidth - 14);
        var gap = layout.LaneCount > 1 ? 8 : 0;
        var laneWidth = Math.Max(92, (availableWidth - gap * (layout.LaneCount - 1)) / layout.LaneCount);
        noteVisual.Width = laneWidth;
        Canvas.SetLeft(noteVisual, 7 + layout.Lane * (laneWidth + gap));
    }

    private FrameworkElement BuildTimelineNote(
        LocalNote note,
        LocalAttachmentSummary? attachmentSummary,
        Brush panelAltBrush,
        Brush inkBrush,
        Brush mutedTextBrush,
        Brush accentBrush,
        Brush accentSoftBrush,
        Brush lineBrush)
    {
        var isSelected = _selectedNoteId == note.Id;
        var hasResizeHandles = _resizeHandlesNoteId == note.Id;
        var startMinute = NoteStartMinute(note);
        var endMinute = NoteEndMinute(note);
        var height = Math.Max(54, (endMinute - startMinute) * MinuteHeight);
        var bodyText = PlainTextFromStoredBody(note.Body);
        var bodyMaxLines = Math.Max(1, (int)Math.Floor(Math.Max(18, height - 52) / 18));

        var host = new Grid
        {
            Height = height,
            Tag = note
        };

        var cardGrid = new Grid
        {
            ColumnSpacing = 10
        };
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        cardGrid.Children.Add(new Border
        {
            Background = isSelected ? accentBrush : accentSoftBrush,
            CornerRadius = new CornerRadius(3)
        });

        var copy = new Grid
        {
            Height = Math.Max(34, height - 20),
            Padding = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Top
        };
        copy.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        copy.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        copy.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(copy, 1);
        cardGrid.Children.Add(copy);

        var headerText = new TextBlock
        {
            Text = $"{note.StartTime:HH:mm}-{(note.EndTime ?? note.StartTime.AddMinutes(GetDefaultDurationMinutes())):HH:mm}  {note.Title}",
            Foreground = inkBrush,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        copy.Children.Add(headerText);

        var bodyBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(bodyText) ? T("noTextYet") : bodyText,
            Foreground = mutedTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = bodyMaxLines
        };
        Grid.SetRow(bodyBlock, 1);
        copy.Children.Add(bodyBlock);

        var photoCount = attachmentSummary?.PhotoCount ?? 0;
        var audioCount = attachmentSummary?.AudioCount ?? 0;
        if (photoCount > 0 || audioCount > 0)
        {
            var mediaRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 3, 0, 0)
            };

            if (photoCount > 0)
            {
                mediaRow.Children.Add(BuildTimelineMediaBadge(Symbol.Pictures, photoCount, mutedTextBrush));
            }

            if (audioCount > 0)
            {
                mediaRow.Children.Add(BuildTimelineMediaBadge(Symbol.Microphone, audioCount, mutedTextBrush));
            }

            Grid.SetRow(mediaRow, 2);
            copy.Children.Add(mediaRow);
        }

        var noteButton = new Button
        {
            Padding = new Thickness(0),
            Background = panelAltBrush,
            BorderBrush = isSelected ? accentBrush : lineBrush,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = note,
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                VerticalAlignment = VerticalAlignment.Top,
                Child = cardGrid
            }
        };
        noteButton.Click += NoteButton_Click;
        noteButton.DoubleTapped += NoteButton_DoubleTapped;
        host.Children.Add(noteButton);

        if (hasResizeHandles)
        {
            var topHandle = BuildResizeHandle(note, host, ResizeEdge.Top, accentBrush);
            topHandle.VerticalAlignment = VerticalAlignment.Top;
            topHandle.Margin = new Thickness(0, -17, 0, 0);
            host.Children.Add(topHandle);

            var bottomHandle = BuildResizeHandle(note, host, ResizeEdge.Bottom, accentBrush);
            bottomHandle.VerticalAlignment = VerticalAlignment.Bottom;
            bottomHandle.Margin = new Thickness(0, 0, 0, -17);
            host.Children.Add(bottomHandle);
        }

        return host;
    }

    private static FrameworkElement BuildTimelineMediaBadge(Symbol symbol, int count, Brush brush)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new SymbolIcon(symbol)
                {
                    Width = 15,
                    Height = 15,
                    Foreground = brush
                },
                new TextBlock
                {
                    Text = count.ToString(),
                    Foreground = brush,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private Border BuildResizeHandle(LocalNote note, FrameworkElement host, ResizeEdge edge, Brush accentBrush)
    {
        var icon = new TextBlock
        {
            Text = edge == ResizeEdge.Top ? "↑" : "↓",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Resources["PanelAltBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Text = edge == ResizeEdge.Top ? "^" : "v";

        var handle = new Border
        {
            Width = 42,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = accentBrush,
            BorderBrush = (Brush)Resources["LineBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Opacity = 0.96,
            Tag = new ResizeContext(note, host, edge),
            Child = icon
        };
        handle.PointerPressed += ResizeHandle_PointerPressed;
        handle.PointerMoved += ResizeHandle_PointerMoved;
        handle.PointerReleased += ResizeHandle_PointerReleased;
        handle.PointerCanceled += ResizeHandle_PointerReleased;
        ToolTipService.SetToolTip(handle, edge == ResizeEdge.Top ? "Drag to change start time" : "Drag to change end time");
        return handle;
    }

    private FrameworkElement BuildHourRow(int hour, IReadOnlyList<LocalNote> notes)
    {
        var lineBrush = (Brush)Resources["LineBrush"];
        var mutedTextBrush = (Brush)Resources["MutedTextBrush"];
        var inkBrush = (Brush)Resources["InkBrush"];
        var panelBrush = (Brush)Resources["PanelAltBrush"];
        var accentBrush = (Brush)Resources["AccentBrush"];
        var accentSoftBrush = (Brush)Resources["AccentSoftBrush"];

        var row = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(0, 4, 0, 4)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = $"{hour:00}:00",
            FontSize = 13,
            Foreground = mutedTextBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0)
        };
        row.Children.Add(label);

        var stack = new StackPanel { Spacing = 6 };
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);

        if (notes.Count == 0)
        {
            var emptySlot = new Button
            {
                Padding = new Thickness(0),
                Background = panelBrush,
                BorderBrush = lineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = hour,
                Content = new Border
                {
                    MinHeight = 34
                }
            };
            emptySlot.Click += EmptyHour_Click;
            emptySlot.DoubleTapped += EmptyHour_DoubleTapped;
            stack.Children.Add(emptySlot);
            return row;
        }

        foreach (var note in notes)
        {
            var isSelected = _selectedNoteId == note.Id;
            var hasResizeHandles = _resizeHandlesNoteId == note.Id;
            var durationMinutes = NoteDurationMinutes(note);
            var visualHeight = Math.Max(58, durationMinutes * 1.15);
            var bodyText = PlainTextFromStoredBody(note.Body);
            var bodyMaxLines = Math.Max(1, (int)Math.Floor(Math.Max(18, visualHeight - 62) / 18));
            var noteGrid = new Grid
            {
                ColumnSpacing = 10
            };
            noteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            noteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new Border
            {
                Background = isSelected ? accentBrush : accentSoftBrush,
                CornerRadius = new CornerRadius(3)
            };
            noteGrid.Children.Add(marker);

            var copy = new StackPanel
            {
                Spacing = 3,
                Padding = new Thickness(0, 2, 0, 2)
            };
            Grid.SetColumn(copy, 1);
            noteGrid.Children.Add(copy);

            copy.Children.Add(new TextBlock
            {
                Text = $"{note.HourLabel}  {note.Title}",
                Foreground = inkBrush,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            copy.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(bodyText) ? T("noTextYet") : bodyText,
                Foreground = mutedTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = bodyMaxLines
            });
            copy.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(note.TranscriptText) ? note.Subtitle : note.TranscriptText,
                Foreground = mutedTextBrush,
                FontSize = 12,
                Opacity = 0.76,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });

            var noteContent = new Grid
            {
                MinHeight = visualHeight
            };
            noteContent.Children.Add(noteGrid);

            var button = new Button
            {
                Padding = new Thickness(0),
                Background = panelBrush,
                BorderBrush = isSelected ? accentBrush : lineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = note,
                Content = new Border
                {
                    Padding = new Thickness(12, 10, 12, 10),
                    Child = noteContent
                }
            };
            button.Click += NoteButton_Click;
            button.DoubleTapped += NoteButton_DoubleTapped;

            if (!hasResizeHandles)
            {
                stack.Children.Add(button);
                continue;
            }

            var selectedNoteHost = new StackPanel
            {
                Spacing = 4
            };
            selectedNoteHost.Children.Add(button);

            var gripDots = new TextBlock
            {
                Text = "...",
                Foreground = (Brush)Resources["PanelAltBrush"],
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -8, 0, 0)
            };

            var resizeHandle = new Border
            {
                Height = 18,
                Margin = new Thickness(28, 0, 28, 0),
                Background = accentBrush,
                CornerRadius = new CornerRadius(7),
                Opacity = 0.9,
                Tag = note,
                Child = gripDots
            };
            resizeHandle.PointerPressed += ResizeHandle_PointerPressed;
            resizeHandle.PointerMoved += ResizeHandle_PointerMoved;
            resizeHandle.PointerReleased += ResizeHandle_PointerReleased;
            resizeHandle.PointerCanceled += ResizeHandle_PointerReleased;
            ToolTipService.SetToolTip(resizeHandle, "Drag to change note length");

            selectedNoteHost.Children.Add(resizeHandle);
            stack.Children.Add(selectedNoteHost);
        }

        return row;
    }

    private async void NoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LocalNote note })
        {
            return;
        }

        await SaveCurrentNoteDraftAsync();
        SelectNote(note, $"Selected {note.Title}.");
        await LoadSelectedMediaAsync(note.Id);
        await RefreshAllNotesPanelAsync();
    }

    private async void NoteButton_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: LocalNote note })
        {
            return;
        }

        await SaveCurrentNoteDraftAsync();
        SelectNote(note, $"Resize handles enabled for {note.Title}.", showResizeHandles: true);
        await LoadSelectedMediaAsync(note.Id);
        await ReloadAsync();
        e.Handled = true;
    }

    private void SelectNote(LocalNote note, string status, bool showResizeHandles = false)
    {
        _selectedNoteId = note.Id;
        _resizeHandlesNoteId = showResizeHandles ? note.Id : null;
        TitleBox.Text = note.Title;
        SetBodyContent(note.Body);
        StartTimePicker.Time = note.StartTime.ToTimeSpan();
        EndTimePicker.Time = note.EndTime?.ToTimeSpan() ?? note.StartTime.AddHours(1).ToTimeSpan();
        SetTranscriptState(note);
        StatusBox.Text = status;
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: ResizeContext context } handle)
        {
            return;
        }

        _resizingNote = context.Note;
        _resizeVisual = context.Host;
        _resizeEdge = context.Edge;
        _resizeStartY = e.GetCurrentPoint(this).Position.Y;
        _resizeOriginalStartMinutes = NoteStartMinute(context.Note);
        _resizeOriginalEndMinutes = NoteEndMinute(context.Note);
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingNote is null || _resizeVisual is null || !e.Pointer.IsInContact)
        {
            return;
        }

        var currentY = e.GetCurrentPoint(this).Position.Y;
        var deltaMinutes = SnapToQuarterHour((currentY - _resizeStartY) / MinuteHeight);
        var startMinute = _resizeOriginalStartMinutes;
        var endMinute = _resizeOriginalEndMinutes;

        if (_resizeEdge == ResizeEdge.Top)
        {
            startMinute = Math.Clamp(_resizeOriginalStartMinutes + deltaMinutes, 0, _resizeOriginalEndMinutes - 15);
        }
        else
        {
            endMinute = Math.Clamp(_resizeOriginalEndMinutes + deltaMinutes, _resizeOriginalStartMinutes + 15, 1439);
        }

        _resizingNote.StartTime = TimeFromMinute(startMinute);
        _resizingNote.EndTime = TimeFromMinute(endMinute);
        StartTimePicker.Time = _resizingNote.StartTime.ToTimeSpan();
        var newEnd = _resizingNote.EndTime.Value;
        EndTimePicker.Time = newEnd.ToTimeSpan();
        Canvas.SetTop(_resizeVisual, startMinute * MinuteHeight);
        _resizeVisual.Height = Math.Max(54, (endMinute - startMinute) * MinuteHeight);
        StatusBox.Text = $"{_resizingNote.StartTime:HH:mm}-{newEnd:HH:mm}. Release to save.";
        e.Handled = true;
    }

    private async void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingNote is null)
        {
            return;
        }

        if (sender is Border handle)
        {
            handle.ReleasePointerCapture(e.Pointer);
        }

        var note = _resizingNote;
        _resizingNote = null;
        _resizeVisual = null;

        await RunUiAsync(async () =>
        {
            var saved = await _store.UpsertNoteAsync(
                note.Id,
                note.Title,
                note.Body,
                note.Date,
                note.StartTime,
                note.EndTime);

            _selectedNoteId = saved.Id;
            _resizeHandlesNoteId = saved.Id;
            StartTimePicker.Time = saved.StartTime.ToTimeSpan();
            EndTimePicker.Time = (saved.EndTime ?? saved.StartTime.AddMinutes(GetDefaultDurationMinutes())).ToTimeSpan();
            StatusBox.Text = $"Resized to {saved.StartTime:HH:mm}-{saved.EndTime:HH:mm}. Saved locally.";
            SyncStateText.Text = "Local changes";
            await ReloadAsync();
        });

        e.Handled = true;
    }

    private async void EmptyHour_Click(object sender, RoutedEventArgs e)
    {
        if ((_selectedNoteId is null && _resizeHandlesNoteId is null) || sender is not Button { Tag: int hour })
        {
            return;
        }

        await SaveCurrentNoteDraftAsync();
        ClearSelectedNote(TimeOnly.FromTimeSpan(TimeSpan.FromHours(hour)));
        await ReloadAsync();
    }

    private async void EmptyHour_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: int hour })
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await SaveCurrentNoteDraftAsync();
            var startTime = new TimeOnly(hour, 0);
            var note = await _store.UpsertNoteAsync(
                null,
                "Untitled note",
                "",
                SelectedDate(),
                startTime,
                startTime.AddMinutes(GetDefaultDurationMinutes()));

            ClearMediaState();
            SelectNote(note, $"Created note at {startTime:HH:mm}. Drag arrows to resize.", showResizeHandles: true);
            SyncStateText.Text = "Local changes";
            await ReloadAsync();
        });

        e.Handled = true;
    }

    private void ClearSelectedNote(TimeOnly startTime)
    {
        _selectedNoteId = null;
        _resizeHandlesNoteId = null;
        TitleBox.Text = "";
        SetBodyContent("");
        StartTimePicker.Time = startTime.ToTimeSpan();
        EndTimePicker.Time = startTime.AddMinutes(GetDefaultDurationMinutes()).ToTimeSpan();
        ClearMediaState();
        SetTranscriptState((LocalNote?)null);
        StatusBox.Text = "Selection cleared.";
    }

    private void StartNewNote(TimeOnly startTime)
    {
        _selectedNoteId = null;
        _resizeHandlesNoteId = null;
        TitleBox.Text = "";
        SetBodyContent("");
        StartTimePicker.Time = startTime.ToTimeSpan();
        EndTimePicker.Time = startTime.AddMinutes(GetDefaultDurationMinutes()).ToTimeSpan();
        ClearMediaState();
        SetTranscriptState((LocalNote?)null);
        StatusBox.Text = $"New separate note at {startTime:HH:mm}.";
    }

    private async Task EnsureCurrentNoteAsync(string fallbackTitle)
    {
        if (_selectedNoteId is not null)
        {
            await SaveCurrentNoteDraftAsync();
            return;
        }

        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? fallbackTitle : TitleBox.Text;
        var note = await _store.UpsertNoteAsync(
            null,
            title,
            GetBodyContent(),
            SelectedDate(),
            TimeOnly.FromTimeSpan(StartTimePicker.Time),
            TimeOnly.FromTimeSpan(EndTimePicker.Time));

        _selectedNoteId = note.Id;
        TitleBox.Text = note.Title;
    }

    private async Task<LocalNote?> SaveCurrentNoteDraftAsync(
        bool updateStatus = false,
        bool force = false,
        DateOnly? dateOverride = null)
    {
        var body = GetBodyContent();
        var title = TitleBox.Text;
        var date = dateOverride ?? SelectedDate();
        var start = TimeOnly.FromTimeSpan(StartTimePicker.Time);
        var end = TimeOnly.FromTimeSpan(EndTimePicker.Time);

        if (_selectedNoteId is null && IsEmptyDraft(title, body))
        {
            return null;
        }

        if (!force && _selectedNoteId is Guid selectedId)
        {
            var existing = await _store.GetNoteByIdAsync(selectedId);
            if (existing is not null
                && existing.DeletedAt is null
                && string.Equals(existing.Title, title, StringComparison.Ordinal)
                && string.Equals(existing.Body, body, StringComparison.Ordinal)
                && existing.Date == date
                && existing.StartTime == start
                && Nullable.Equals(existing.EndTime, end))
            {
                return existing;
            }
        }

        var note = await _store.UpsertNoteAsync(_selectedNoteId, title, body, date, start, end);
        _selectedNoteId = note.Id;
        SetTranscriptState(note);
        SyncStateText.Text = "Local changes";
        if (updateStatus)
        {
            StatusBox.Text = "Saved locally.";
        }

        return note;
    }

    private static bool IsEmptyDraft(string title, string body)
    {
        return string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(PlainTextFromStoredBody(body));
    }

    private string GetBodyContent()
    {
        BodyBox.Document.GetText(TextGetOptions.None, out var plain);
        BodyBox.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        plain = (plain ?? "").TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(plain))
        {
            return "";
        }

        return JsonSerializer.Serialize(new StoredRichBody("rtf", plain, rtf ?? ""));
    }

    private void SetBodyContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            BodyBox.Document.SetText(TextSetOptions.None, "");
            return;
        }

        if (TryReadStoredRichBody(content, out var stored))
        {
            if (!string.IsNullOrWhiteSpace(stored.Rtf))
            {
                try
                {
                    BodyBox.Document.SetText(TextSetOptions.FormatRtf, stored.Rtf);
                    BodyBox.Document.GetText(TextGetOptions.None, out var renderedPlain);
                    if (!string.IsNullOrWhiteSpace(renderedPlain))
                    {
                        return;
                    }
                }
                catch
                {
                    // Use plain text below if the platform rejects this RTF payload.
                }
            }

            BodyBox.Document.SetText(TextSetOptions.None, stored.Plain);
            return;
        }

        if (IsStoredRtf(content))
        {
            try
            {
                BodyBox.Document.SetText(TextSetOptions.FormatRtf, content);
                return;
            }
            catch
            {
                // Fall back to plain text if an older local row contains malformed RTF.
            }
        }

        if (!TrySetLegacyMarkdownBodyContent(content))
        {
            BodyBox.Document.SetText(TextSetOptions.None, content);
        }
    }

    private bool TrySetLegacyMarkdownBodyContent(string content)
    {
        var canConvertBold = Regex.Matches(content, @"\*\*").Count is var boldMarkerCount && boldMarkerCount > 1 && boldMarkerCount % 2 == 0;
        var canConvertItalic = content.Count(x => x == '_') is var italicMarkerCount && italicMarkerCount > 1 && italicMarkerCount % 2 == 0;
        if (!canConvertBold && !canConvertItalic)
        {
            return false;
        }

        BodyBox.Document.SetText(TextSetOptions.None, "");
        var segment = new StringBuilder();
        var bold = false;
        var italic = false;

        for (var i = 0; i < content.Length; i++)
        {
            if (canConvertBold && i + 1 < content.Length && content[i] == '*' && content[i + 1] == '*')
            {
                TypeRichSegment(segment.ToString(), bold, italic);
                segment.Clear();
                bold = !bold;
                i++;
                continue;
            }

            if (canConvertItalic && content[i] == '_')
            {
                TypeRichSegment(segment.ToString(), bold, italic);
                segment.Clear();
                italic = !italic;
                continue;
            }

            segment.Append(content[i]);
        }

        TypeRichSegment(segment.ToString(), bold, italic);
        return true;
    }

    private void TypeRichSegment(string text, bool bold, bool italic)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var format = BodyBox.Document.Selection.CharacterFormat;
        format.Bold = bold ? FormatEffect.On : FormatEffect.Off;
        format.Italic = italic ? FormatEffect.On : FormatEffect.Off;
        BodyBox.Document.Selection.TypeText(text);
    }

    private static string PlainTextFromStoredBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        if (TryReadStoredRichBody(body, out var stored))
        {
            return stored.Plain.Trim();
        }

        if (!IsStoredRtf(body))
        {
            return body.Trim();
        }

        var builder = new StringBuilder();
        var skipDepth = 0;

        for (var i = 0; i < body.Length; i++)
        {
            var current = body[i];
            if (current == '{')
            {
                if (skipDepth > 0)
                {
                    skipDepth++;
                    continue;
                }

                if (IsIgnorableRtfGroup(body, i))
                {
                    skipDepth = 1;
                }

                continue;
            }

            if (current == '}')
            {
                if (skipDepth > 0)
                {
                    skipDepth--;
                }

                continue;
            }

            if (skipDepth > 0)
            {
                continue;
            }

            if (current != '\\')
            {
                builder.Append(current);
                continue;
            }

            if (i + 1 >= body.Length)
            {
                continue;
            }

            var next = body[++i];
            if (next is '\\' or '{' or '}')
            {
                builder.Append(next);
                continue;
            }

            if (next == '\'')
            {
                i += Math.Min(2, body.Length - i - 1);
                continue;
            }

            if (!char.IsLetter(next))
            {
                if (next == '~')
                {
                    builder.Append(' ');
                }

                continue;
            }

            var wordStart = i;
            while (i + 1 < body.Length && char.IsLetter(body[i + 1]))
            {
                i++;
            }

            var word = body[wordStart..(i + 1)];
            var sign = 1;
            var number = 0;
            var hasNumber = false;
            if (i + 1 < body.Length && body[i + 1] == '-')
            {
                sign = -1;
                i++;
            }

            while (i + 1 < body.Length && char.IsDigit(body[i + 1]))
            {
                hasNumber = true;
                number = number * 10 + (body[i + 1] - '0');
                i++;
            }

            if (i + 1 < body.Length && body[i + 1] == ' ')
            {
                i++;
            }

            switch (word)
            {
                case "par":
                case "pard":
                case "line":
                    builder.AppendLine();
                    break;
                case "tab":
                    builder.Append('\t');
                    break;
                case "u" when hasNumber:
                    var code = sign * number;
                    builder.Append(char.ConvertFromUtf32(code < 0 ? code + 65536 : code));
                    if (i + 1 < body.Length && body[i + 1] != '\\' && body[i + 1] != '{' && body[i + 1] != '}')
                    {
                        i++;
                    }
                    break;
            }
        }

        return Regex.Replace(builder.ToString(), @"[ \t]+\r?\n", Environment.NewLine).Trim();
    }

    private static bool IsStoredRtf(string value) =>
        value.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal);

    private static bool TryReadStoredRichBody(string value, out StoredRichBody stored)
    {
        stored = new StoredRichBody("", "", "");
        if (!value.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<StoredRichBody>(value);
            if (parsed is null || !string.Equals(parsed.Format, "rtf", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            stored = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsIgnorableRtfGroup(string rtf, int openBraceIndex)
    {
        var i = openBraceIndex + 1;
        if (i >= rtf.Length || rtf[i] != '\\')
        {
            return false;
        }

        i++;
        if (i < rtf.Length && rtf[i] == '*')
        {
            i++;
            if (i < rtf.Length && rtf[i] == '\\')
            {
                i++;
            }
        }

        var start = i;
        while (i < rtf.Length && char.IsLetter(rtf[i]))
        {
            i++;
        }

        var destination = rtf[start..i];
        return destination is "fonttbl" or "colortbl" or "stylesheet" or "generator" or "info"
            or "pict" or "object" or "filetbl" or "revtbl" or "rsidtbl" or "listtable"
            or "listoverridetable" or "header" or "footer" or "pntext";
    }

    private DateOnly SelectedDate() => _selectedDate;

    private static int SnapToQuarterHour(double minutes)
    {
        return (int)(Math.Round(minutes / 15.0) * 15);
    }

    private static int NoteStartMinute(LocalNote note)
    {
        return (int)note.StartTime.ToTimeSpan().TotalMinutes;
    }

    private int NoteEndMinute(LocalNote note)
    {
        var end = note.EndTime ?? note.StartTime.AddMinutes(GetDefaultDurationMinutes());
        var minute = (int)end.ToTimeSpan().TotalMinutes;
        return Math.Clamp(minute, NoteStartMinute(note) + 1, 1439);
    }

    private static TimeOnly TimeFromMinute(int minute)
    {
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(minute, 0, 1439)));
    }

    private static int ClampDuration(TimeOnly start, int proposedMinutes)
    {
        var maxDuration = Math.Max(1, 1439 - (int)start.ToTimeSpan().TotalMinutes);
        if (maxDuration < 15)
        {
            return maxDuration;
        }

        return Math.Clamp(proposedMinutes, 15, maxDuration);
    }

    private int NoteDurationMinutes(LocalNote note)
    {
        var end = note.EndTime ?? note.StartTime.AddMinutes(GetDefaultDurationMinutes());
        var minutes = (int)(end.ToTimeSpan() - note.StartTime.ToTimeSpan()).TotalMinutes;
        return minutes > 0 ? minutes : GetDefaultDurationMinutes();
    }

    private void SetRecordButtonContent(Symbol symbol, string text)
    {
        RecordButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new SymbolIcon(symbol),
                new TextBlock { Text = text }
            }
        };
    }

    private void BoldText_Click(object sender, RoutedEventArgs e)
    {
        ToggleCharacterFormat(format => format.Bold = FormatEffect.Toggle);
    }

    private void ItalicText_Click(object sender, RoutedEventArgs e)
    {
        ToggleCharacterFormat(format => format.Italic = FormatEffect.Toggle);
    }

    private void HeadingText_Click(object sender, RoutedEventArgs e)
    {
        ApplyCharacterFormat(format =>
        {
            format.Bold = FormatEffect.On;
            format.Size = 20;
        });
    }

    private void BulletedList_Click(object sender, RoutedEventArgs e)
    {
        PrefixBodyLines("- ");
    }

    private void NumberedList_Click(object sender, RoutedEventArgs e)
    {
        PrefixBodyLines("", numbered: true);
    }

    private void Checklist_Click(object sender, RoutedEventArgs e)
    {
        PrefixBodyLines("- [ ] ");
    }

    private void QuoteText_Click(object sender, RoutedEventArgs e)
    {
        PrefixBodyLines("> ");
    }

    private void CodeText_Click(object sender, RoutedEventArgs e)
    {
        ApplyCharacterFormat(format =>
        {
            format.Name = "Consolas";
            format.BackgroundColor = Color.FromArgb(255, 230, 247, 245);
        });
    }

    private void LinkText_Click(object sender, RoutedEventArgs e)
    {
        InsertLinkMarkup();
    }

    private void BoldText_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleCharacterFormat(format => format.Bold = FormatEffect.Toggle);
        args.Handled = true;
    }

    private void ItalicText_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleCharacterFormat(format => format.Italic = FormatEffect.Toggle);
        args.Handled = true;
    }

    private void LinkText_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        InsertLinkMarkup();
        args.Handled = true;
    }

    private void TableText_Click(object sender, RoutedEventArgs e)
    {
        var table = string.Join(Environment.NewLine, [
            "",
            "| " + T("tableColumn") + " 1 | " + T("tableColumn") + " 2 |",
            "| --- | --- |",
            "| " + T("tableCell") + " | " + T("tableCell") + " |",
            ""
        ]);
        InsertBodyText(table);
    }

    private string SelectedBodyText()
    {
        BodyBox.Document.Selection.GetText(TextGetOptions.None, out var selected);
        return selected ?? "";
    }

    private void PrefixBodyLines(string prefix, bool numbered = false)
    {
        var selected = SelectedBodyText();
        var body = string.IsNullOrEmpty(selected) ? "" : selected;
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var formatted = lines
            .Select((line, index) => numbered ? $"{index + 1}. {line}" : $"{prefix}{line}")
            .ToArray();
        InsertBodyText(string.Join(Environment.NewLine, formatted));
    }

    private void InsertLinkMarkup()
    {
        var selected = SelectedBodyText();
        if (string.IsNullOrEmpty(selected))
        {
            InsertBodyText("https://");
            return;
        }

        InsertBodyText($"{selected} (https://)");
    }

    private void InsertBodyText(string text)
    {
        BodyBox.Document.Selection.TypeText(text);
        BodyBox.Focus(FocusState.Programmatic);
    }

    private void ToggleCharacterFormat(Action<ITextCharacterFormat> update)
    {
        update(BodyBox.Document.Selection.CharacterFormat);
        BodyBox.Focus(FocusState.Programmatic);
    }

    private void ApplyCharacterFormat(Action<ITextCharacterFormat> update)
    {
        update(BodyBox.Document.Selection.CharacterFormat);
        BodyBox.Focus(FocusState.Programmatic);
    }

    private async void PlayAudio_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            if (_selectedAudioAttachment is null)
            {
                StatusBox.Text = "This note has no local audio yet.";
                return;
            }

            await PlayAudioAttachmentAsync(_selectedAudioAttachment);
        });
    }

    private void StopAudio_Click(object sender, RoutedEventArgs e)
    {
        _audioPlayer?.Pause();
        if (_audioPlayer is not null)
        {
            _audioPlayer.Source = null;
        }

        AudioPlayerElement.Visibility = Visibility.Collapsed;
        AudioStatusText.Text = _selectedAudioAttachment is null ? T("noAudioAttached") : T("readyToPlayAudio");
        SetMediaAction(_selectedAudioAttachment is null ? T("noAudioAttached") : T("readyToPlayAudio"), false);
        StatusBox.Text = "Audio stopped.";
    }

    private async Task LoadSelectedMediaAsync(Guid noteId)
    {
        StopAudioPlayback();
        var attachments = await _store.GetAttachmentsForNoteAsync(noteId);
        var transcripts = await _store.GetTranscriptsForNoteAsync(noteId);
        var audioAttachments = attachments
            .Where(x => x.Type == AttachmentType.Audio && File.Exists(x.LocalPath))
            .OrderByDescending(AudioSortTime)
            .ToArray();
        if (_expandedAudioAttachmentId is not null && audioAttachments.All(x => x.Id != _expandedAudioAttachmentId.Value))
        {
            _expandedAudioAttachmentId = null;
        }

        _selectedAudioAttachment = _selectedAudioAttachment is null
            ? audioAttachments.FirstOrDefault()
            : audioAttachments.FirstOrDefault(x => x.Id == _selectedAudioAttachment.Id) ?? audioAttachments.FirstOrDefault();

        _expandedAudioAttachmentId ??= _selectedAudioAttachment?.Id;

        var hasAudio = _selectedAudioAttachment is not null;
        PlayAudioButton.IsEnabled = hasAudio;
        StopAudioButton.IsEnabled = hasAudio;
        AudioPlayerElement.Visibility = Visibility.Collapsed;
        AudioStatusText.Text = hasAudio
            ? $"{audioAttachments.Length} audio attached. Select a recording below."
            : T("noAudioAttached");
        SetMediaAction(hasAudio ? "Audio ready. Choose a recording to play." : "Record audio or attach photos to this note.", false);

        RenderExpandableAudioList(audioAttachments, transcripts);
        RenderPhotos(attachments);
        UpdateMediaIndicator(attachments);
    }

    private void RenderExpandableAudioList(
        IReadOnlyList<LocalAttachment> audioAttachments,
        IReadOnlyDictionary<Guid, LocalTranscript> transcripts)
    {
        AudioListPanel.Children.Clear();
        if (audioAttachments.Count == 0)
        {
            AudioListPanel.Children.Add(new Border
            {
                Padding = new Thickness(10),
                Background = (Brush)Resources["PanelAltBrush"],
                BorderBrush = (Brush)Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = "No audio recordings yet",
                    Foreground = (Brush)Resources["MutedTextBrush"],
                    TextWrapping = TextWrapping.Wrap
                }
            });
            return;
        }

        for (var index = 0; index < audioAttachments.Count; index++)
        {
            var attachment = audioAttachments[index];
            transcripts.TryGetValue(attachment.Id, out var transcript);
            var title = AudioDisplayName(attachment, index);
            var status = AudioTranscriptStatusText(transcript);
            var preview = AudioTranscriptPreviewText(transcript);
            var isExpanded = _expandedAudioAttachmentId == attachment.Id;

            var headerGrid = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            headerGrid.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = $"{status} - {preview}",
                        Foreground = (Brush)Resources["MutedTextBrush"],
                        FontSize = 12,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            });

            var chevron = new FontIcon
            {
                Glyph = isExpanded ? "\uE70D" : "\uE76C",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(chevron, 1);
            headerGrid.Children.Add(chevron);

            var headerButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = new AudioTranscriptSelection(attachment, transcript),
                Content = headerGrid
            };

            var controlRow = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                }
            };
            var playButton = CreateAudioControlButton(Symbol.Play, "Play", new AudioControlContext(attachment, transcript, AudioListCommand.Play));
            var pauseButton = CreateAudioControlButton(Symbol.Pause, "Pause", new AudioControlContext(attachment, transcript, AudioListCommand.Pause));
            var stopButton = CreateAudioControlButton(Symbol.Stop, "Stop", new AudioControlContext(attachment, transcript, AudioListCommand.Stop));
            Grid.SetColumn(pauseButton, 1);
            Grid.SetColumn(stopButton, 2);
            controlRow.Children.Add(playButton);
            controlRow.Children.Add(pauseButton);
            controlRow.Children.Add(stopButton);

            var nameBox = new TextBox
            {
                Header = "Audio name",
                Text = AudioDisplayName(attachment, index),
                PlaceholderText = "Audio name"
            };
            nameBox.KeyDown += async (_, e) =>
            {
                if (e.Key != VirtualKey.Enter)
                {
                    return;
                }

                e.Handled = true;
                await SaveAudioNameFromBoxAsync(attachment, nameBox, audioAttachments, transcripts);
            };

            var transcriptBox = new Border
            {
                Padding = new Thickness(10),
                Background = (Brush)Resources["InputBrush"],
                BorderBrush = (Brush)Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = new TextBlock
                {
                    Text = preview,
                    Foreground = (Brush)Resources["MutedTextBrush"],
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    MaxLines = 8
                }
            };

            var contentPanel = new StackPanel
            {
                Spacing = 8,
                Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed,
                Children =
                {
                    nameBox,
                    controlRow,
                    new TextBlock
                    {
                        Text = "Transcript",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = (Brush)Resources["MutedTextBrush"],
                        FontSize = 12
                    },
                    transcriptBox
                }
            };

            headerButton.Click += (_, _) =>
            {
                var shouldOpen = _expandedAudioAttachmentId != attachment.Id;
                _expandedAudioAttachmentId = shouldOpen ? attachment.Id : null;
                SelectAudioTranscript(attachment, transcript, shouldOpen ? "Selected audio." : "Audio collapsed.");
                RenderExpandableAudioList(audioAttachments, transcripts);
            };

            AudioListPanel.Children.Add(new Border
            {
                Padding = new Thickness(8),
                Background = isExpanded ? (Brush)Resources["AccentSoftBrush"] : (Brush)Resources["PanelAltBrush"],
                BorderBrush = isExpanded ? (Brush)Resources["AccentBrush"] : (Brush)Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        headerButton,
                        contentPanel
                    }
                }
            });
        }
    }

    private Button CreateAudioControlButton(Symbol symbol, string label, AudioControlContext context)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 32,
            Padding = new Thickness(8, 5, 8, 5),
            Tag = context,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new SymbolIcon(symbol),
                    new TextBlock
                    {
                        Text = label,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
        button.Click += AudioControl_Click;
        return button;
    }

    private async Task RenameAudioAttachmentAsync(LocalAttachment attachment, string requestedName)
    {
        var normalized = NormalizeAudioFileName(requestedName, attachment);
        if (string.Equals(normalized, attachment.FileName, StringComparison.Ordinal))
        {
            StatusBox.Text = "Audio name is unchanged.";
            return;
        }

        await _store.UpdateAttachmentFileNameAsync(attachment.Id, normalized);
        if (_api.IsSignedIn && attachment.IsUploaded)
        {
            await _api.RenameAttachmentAsync(attachment.Id, normalized);
        }

        attachment.FileName = normalized;
        AudioStatusText.Text = $"Selected {AudioStatusName(attachment)}";
        StatusBox.Text = "Audio name saved.";
    }

    private async Task SaveAudioNameFromBoxAsync(
        LocalAttachment attachment,
        TextBox nameBox,
        IReadOnlyList<LocalAttachment> audioAttachments,
        IReadOnlyDictionary<Guid, LocalTranscript> transcripts)
    {
        await RunUiAsync(async () =>
        {
            await RenameAudioAttachmentAsync(attachment, nameBox.Text);
            attachment.FileName = NormalizeAudioFileName(nameBox.Text, attachment);
            RenderExpandableAudioList(audioAttachments, transcripts);
        });
    }

    private static string AudioDisplayName(LocalAttachment attachment, int index)
    {
        var name = Path.GetFileNameWithoutExtension(attachment.FileName);
        if (string.IsNullOrWhiteSpace(name) || LooksGeneratedAudioName(name))
        {
            return $"Recording {index + 1} - {AudioSortTime(attachment).ToLocalTime():HH:mm}";
        }

        return name;
    }

    private static string AudioStatusName(LocalAttachment attachment)
    {
        var name = Path.GetFileNameWithoutExtension(attachment.FileName);
        return string.IsNullOrWhiteSpace(name) || LooksGeneratedAudioName(name)
            ? $"Recording {AudioSortTime(attachment).ToLocalTime():HH:mm}"
            : name;
    }

    private static string NormalizeAudioFileName(string requestedName, LocalAttachment attachment)
    {
        var name = Path.GetFileName(requestedName.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        if (string.IsNullOrWhiteSpace(name) || LooksGeneratedAudioName(Path.GetFileNameWithoutExtension(name)))
        {
            name = $"Recording {AudioSortTime(attachment).ToLocalTime():HH-mm}";
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
        {
            var extension = Path.GetExtension(attachment.FileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = Path.GetExtension(attachment.LocalPath);
            }

            if (!string.IsNullOrWhiteSpace(extension))
            {
                name = $"{name}{extension}";
            }
        }

        return name.Length > 260 ? name[..260] : name;
    }

    private static bool LooksGeneratedAudioName(string name)
    {
        var compact = name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return compact.Length >= 24 && compact.All(Uri.IsHexDigit);
    }

    private static DateTime AudioSortTime(LocalAttachment attachment)
    {
        try
        {
            return File.GetCreationTimeUtc(attachment.LocalPath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string AudioTranscriptPreviewText(LocalTranscript? transcript)
    {
        if (!string.IsNullOrWhiteSpace(transcript?.Text))
        {
            return transcript.Text;
        }

        if (!string.IsNullOrWhiteSpace(transcript?.ErrorMessage))
        {
            return transcript.ErrorMessage;
        }

        return "Transcript will appear after sync.";
    }

    private string AudioTranscriptStatusText(LocalTranscript? transcript)
    {
        return transcript?.Status switch
        {
            TranscriptStatus.Pending => T("queued"),
            TranscriptStatus.Processing => T("processing"),
            TranscriptStatus.Done => T("ready"),
            TranscriptStatus.Failed => T("failed"),
            _ => T("noTranscriptYet")
        };
    }

    private async void AudioControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AudioControlContext context } button)
        {
            return;
        }

        await AnimateAudioButtonPressAsync(button);
        await RunUiAsync(async () =>
        {
            SelectAudioTranscript(context.Attachment, context.Transcript, "Selected audio.");
            _expandedAudioAttachmentId = context.Attachment.Id;

            switch (context.Command)
            {
                case AudioListCommand.Play:
                    await PlayAudioAttachmentAsync(context.Attachment);
                    break;
                case AudioListCommand.Pause:
                    await TogglePauseAudioPlaybackAsync(context.Attachment);
                    break;
                case AudioListCommand.Stop:
                    StopSelectedAudioPlayback(context.Attachment);
                    break;
            }
        });
    }

    private void SelectAudioTranscript(LocalAttachment attachment, LocalTranscript? transcript, string message)
    {
        _selectedAudioAttachment = attachment;
        PlayAudioButton.IsEnabled = true;
        StopAudioButton.IsEnabled = true;
        AudioStatusText.Text = $"Selected {AudioStatusName(attachment)}";
        SetTranscriptState(transcript);
        SetMediaAction(message, false);
    }

    private async Task TogglePauseAudioPlaybackAsync(LocalAttachment attachment)
    {
        if (_audioPlayer is not null
            && _loadedAudioAttachmentId == attachment.Id
            && _audioPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _audioPlayer.Pause();
            AudioStatusText.Text = $"Paused: {AudioStatusName(attachment)}";
            SetMediaAction("Audio paused.", false);
            StatusBox.Text = "Audio paused.";
            return;
        }

        await PlayAudioAttachmentAsync(attachment);
        AudioStatusText.Text = $"Resumed: {AudioStatusName(attachment)}";
        SetMediaAction("Audio resumed.", false);
        StatusBox.Text = "Audio resumed.";
    }

    private static async Task AnimateAudioButtonPressAsync(Button button)
    {
        var previousOpacity = button.Opacity;
        button.Opacity = 0.62;
        await Task.Delay(90);
        button.Opacity = previousOpacity;
    }

    private void PauseAudioPlayback(LocalAttachment attachment)
    {
        _audioPlayer?.Pause();
        AudioStatusText.Text = $"Paused: {AudioStatusName(attachment)}";
        SetMediaAction("Audio paused.", false);
        StatusBox.Text = "Audio paused.";
    }

    private void StopSelectedAudioPlayback(LocalAttachment attachment)
    {
        if (_audioPlayer is not null && _loadedAudioAttachmentId == attachment.Id)
        {
            _audioPlayer.Pause();
            _audioPlayer.Source = null;
            _loadedAudioAttachmentId = null;
        }

        AudioStatusText.Text = $"Stopped: {AudioStatusName(attachment)}";
        SetMediaAction("Audio stopped.", false);
        StatusBox.Text = "Audio stopped.";
    }

    private void RenderAudioList(
        IReadOnlyList<LocalAttachment> audioAttachments,
        IReadOnlyDictionary<Guid, LocalTranscript> transcripts)
    {
        AudioListPanel.Children.Clear();
        if (audioAttachments.Count == 0)
        {
            return;
        }

        for (var index = 0; index < audioAttachments.Count; index++)
        {
            var attachment = audioAttachments[index];
            transcripts.TryGetValue(attachment.Id, out var transcript);
            var title = $"Audio {audioAttachments.Count - index}";
            var status = transcript?.Status switch
            {
                TranscriptStatus.Pending => T("queued"),
                TranscriptStatus.Processing => T("processing"),
                TranscriptStatus.Done => T("ready"),
                TranscriptStatus.Failed => T("failed"),
                _ => T("noTranscriptYet")
            };
            var preview = !string.IsNullOrWhiteSpace(transcript?.Text)
                ? transcript.Text
                : transcript?.ErrorMessage ?? "Transcript will appear after sync.";

            var playButton = new Button
            {
                MinWidth = 84,
                Tag = attachment,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new SymbolIcon(Symbol.Play),
                        new TextBlock { Text = "Play" }
                    }
                }
            };
            playButton.Click += AudioItemPlay_Click;

            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            var openButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = new AudioTranscriptSelection(attachment, transcript),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{title} · {AudioStatusName(attachment)}",
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        new TextBlock
                        {
                            Text = $"{status} · {preview}",
                            Foreground = (Brush)Resources["MutedTextBrush"],
                            FontSize = 12,
                            MaxLines = 2,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
            openButton.Click += AudioItem_Click;
            row.Children.Add(openButton);
            Grid.SetColumn(playButton, 1);
            row.Children.Add(playButton);
            AudioListPanel.Children.Add(row);
        }
    }

    private async void AudioItemPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LocalAttachment attachment })
        {
            await RunUiAsync(async () => await PlayAudioAttachmentAsync(attachment));
        }
    }

    private void AudioItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AudioTranscriptSelection selection })
        {
            return;
        }

        _selectedAudioAttachment = selection.Attachment;
        PlayAudioButton.IsEnabled = true;
        StopAudioButton.IsEnabled = true;
        AudioStatusText.Text = $"Selected {AudioStatusName(selection.Attachment)}";
        SetTranscriptState(selection.Transcript);
        SetMediaAction("Selected audio. Press Play to listen.", false);
    }

    private async Task PlayAudioAttachmentAsync(LocalAttachment attachment)
    {
        _selectedAudioAttachment = attachment;
        if (!File.Exists(attachment.LocalPath))
        {
            StatusBox.Text = "Audio file is missing from local storage.";
            await LoadSelectedMediaAsync(attachment.NoteId);
            return;
        }

        _audioPlayer ??= CreateAudioPlayer();
        AudioPlayerElement.SetMediaPlayer(_audioPlayer);
        AudioPlayerElement.Visibility = Visibility.Collapsed;
        if (_loadedAudioAttachmentId != attachment.Id || _audioPlayer.Source is null)
        {
            var file = await StorageFile.GetFileFromPathAsync(attachment.LocalPath);
            _audioPlayer.Source = MediaSource.CreateFromStorageFile(file);
            _loadedAudioAttachmentId = attachment.Id;
        }

        _audioPlayer.Play();
        AudioStatusText.Text = $"{T("playingAudio")}: {AudioStatusName(attachment)}";
        SetMediaAction(T("playingAudio"), false);
        StatusBox.Text = "Playing selected audio.";
    }

    private void ClearMediaState()
    {
        StopAudioPlayback();
        _selectedAudioAttachment = null;
        _expandedAudioAttachmentId = null;
        PlayAudioButton.IsEnabled = false;
        StopAudioButton.IsEnabled = false;
        AudioPlayerElement.Visibility = Visibility.Collapsed;
        AudioListPanel.Children.Clear();
        AudioStatusText.Text = T("noAudioAttached");
        PhotosPanel.Children.Clear();
        PhotoStatusText.Text = T("noPhotosYet");
        MediaStateText.Text = T("none");
        SetMediaAction("Record audio or attach photos to this note.", false);
    }

    private async Task RefreshSelectedNoteDetailsAsync()
    {
        if (_selectedNoteId is null)
        {
            SetTranscriptState((LocalNote?)null);
            return;
        }

        var note = await _store.GetNoteByIdAsync(_selectedNoteId.Value);
        if (note is null)
        {
            SetTranscriptState((LocalNote?)null);
            return;
        }

        TitleBox.Text = note.Title;
        SetBodyContent(note.Body);
        SetTranscriptState(note);
    }

    private void SetTranscriptState(LocalNote? note)
    {
        if (note is null)
        {
            TranscriptStatusText.Text = T("noTranscriptYet");
            TranscriptBox.Text = "";
            TranscriptStateText.Text = T("noAudio");
            return;
        }

        TranscriptStatusText.Text = note.TranscriptStatus switch
        {
            TranscriptStatus.Pending => T("transcriptQueued"),
            TranscriptStatus.Processing => T("transcriptionInProgress"),
            TranscriptStatus.Done => T("transcriptReady"),
            TranscriptStatus.Failed => T("transcriptionFailed"),
            _ => T("noTranscriptYet")
        };
        TranscriptBox.Text = note.TranscriptText ?? "";
        TranscriptStateText.Text = note.TranscriptStatus switch
        {
            TranscriptStatus.Pending => T("queued"),
            TranscriptStatus.Processing => T("processing"),
            TranscriptStatus.Done => T("ready"),
            TranscriptStatus.Failed => T("failed"),
            _ => T("noAudio")
        };
    }

    private void SetTranscriptState(LocalTranscript? transcript)
    {
        if (transcript is null)
        {
            TranscriptStatusText.Text = T("noTranscriptYet");
            TranscriptBox.Text = "";
            TranscriptStateText.Text = T("noAudio");
            return;
        }

        TranscriptStatusText.Text = transcript.Status switch
        {
            TranscriptStatus.Pending => T("transcriptQueued"),
            TranscriptStatus.Processing => T("transcriptionInProgress"),
            TranscriptStatus.Done => T("transcriptReady"),
            TranscriptStatus.Failed => T("transcriptionFailed"),
            _ => T("noTranscriptYet")
        };
        TranscriptBox.Text = transcript.Text ?? transcript.ErrorMessage ?? "";
        TranscriptStateText.Text = transcript.Status switch
        {
            TranscriptStatus.Pending => T("queued"),
            TranscriptStatus.Processing => T("processing"),
            TranscriptStatus.Done => T("ready"),
            TranscriptStatus.Failed => T("failed"),
            _ => T("noAudio")
        };
    }

    private void UpdateMediaIndicator(IReadOnlyList<LocalAttachment> attachments)
    {
        var photos = attachments.Count(x => x.Type == AttachmentType.Photo && File.Exists(x.LocalPath));
        var audio = attachments.Count(x => x.Type == AttachmentType.Audio && File.Exists(x.LocalPath));
        MediaStateText.Text = (photos, audio) switch
        {
            (0, 0) => T("none"),
            (0, _) => $"{audio} {T("audio")}",
            (_, 0) => $"{photos} {T("photo")}",
            _ => $"{photos} {T("photo")} / {audio} {T("audio")}"
        };
    }

    private void SetMediaAction(string message, bool busy)
    {
        if (MediaActionBorder is null)
        {
            return;
        }

        MediaActionBorder.Visibility = Visibility.Visible;
        MediaActionText.Text = message;
        MediaActionRing.IsActive = busy;
        MediaActionRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearMediaAction()
    {
        if (MediaActionBorder is null)
        {
            return;
        }

        MediaActionBorder.Visibility = Visibility.Collapsed;
        MediaActionText.Text = "";
        MediaActionRing.IsActive = false;
        MediaActionRing.Visibility = Visibility.Collapsed;
    }

    private ComboBox SettingComboBox(string header, (string Label, string Value)[] items, string selectedValue)
    {
        var box = new ComboBox
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        foreach (var item in items)
        {
            box.Items.Add(new ComboBoxItem
            {
                Content = item.Label,
                Tag = item.Value
            });
        }

        SelectComboBoxValue(box, selectedValue);
        return box;
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        await Task.Delay(800);
        var result = await CheckForUpdatesAsync();
        if (result.Success)
        {
            StatusBox.Text = result.Message;
        }
    }

    private async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        try
        {
            var (remoteSha, message) = await GetRemoteUpdateRevisionAsync();
            var shortRemote = ShortSha(remoteSha);
            var localSha = await TryGetLocalGitShaAsync();
            var shortLocal = ShortSha(localSha);

            if (string.IsNullOrWhiteSpace(localSha))
            {
                return new UpdateCheckResult(
                    true,
                    false,
                    $"{T("latestGithubVersion")}: {shortRemote}. {T("currentBuildUnknown")}",
                    localSha,
                    remoteSha);
            }

            if (string.Equals(localSha, "local", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheckResult(
                    true,
                    false,
                    $"{T("latestGithubVersion")}: {shortRemote}. {T("currentBuildUnknown")}",
                    localSha,
                    remoteSha);
            }

            var isCurrent = remoteSha.StartsWith(localSha, StringComparison.OrdinalIgnoreCase)
                || localSha.StartsWith(remoteSha, StringComparison.OrdinalIgnoreCase);

            return isCurrent
                ? new UpdateCheckResult(true, false, $"{T("appUpToDate")} {shortLocal}.", localSha, remoteSha)
                : new UpdateCheckResult(true, true, $"{T("updateAvailable")} {shortLocal} -> {shortRemote}. {message}", localSha, remoteSha);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, $"{T("updateCheckFailed")}: {ex.Message}", null, null);
        }
    }

    private async Task<string> DownloadAndLaunchUpdateInstallerAsync(UpdateCheckResult? knownResult)
    {
        try
        {
            var result = knownResult;
            if (result is null || !result.Success)
            {
                result = await CheckForUpdatesAsync();
            }

            if (!result.Success)
            {
                return result.Message;
            }

            if (!result.UpdateAvailable)
            {
                return result.Message;
            }

            var downloadUrl = result.InstallerDownloadUrl ?? await GetLatestInstallerDownloadUrlAsync();
            var updateDirectory = Path.Combine(Path.GetTempPath(), "NotesMuchachos", "Updates");
            Directory.CreateDirectory(updateDirectory);

            var remoteSuffix = ShortSha(result.RemoteSha);
            var fileName = string.Equals(remoteSuffix, "unknown", StringComparison.OrdinalIgnoreCase)
                ? UpdateInstallerAssetName
                : $"NotesMuchachosSetup-{remoteSuffix}.exe";
            var installerPath = Path.Combine(updateDirectory, fileName);

            using (var request = CreateGitHubRequest(downloadUrl))
            using (var response = await UpdateHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var downloadStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = File.Create(installerPath);
                await downloadStream.CopyToAsync(fileStream);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });

            _ = Task.Delay(900).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
            });

            return T("installerStarted");
        }
        catch (Exception ex)
        {
            return $"{T("updateInstallFailed")}: {ex.Message}";
        }
    }

    private async Task<string> GetLatestInstallerDownloadUrlAsync()
    {
        using var request = CreateGitHubRequest(UpdateLatestReleaseUrl);
        using var response = await UpdateHttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        if (document.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                if (!string.Equals(name, UpdateInstallerAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(downloadUrl))
                {
                    return downloadUrl;
                }
            }
        }

        throw new InvalidOperationException(T("installerAssetMissing"));
    }

    private static HttpRequestMessage CreateGitHubRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NotesMuchachos", "1.0"));
        return request;
    }

    private async Task<(string Sha, string Message)> GetRemoteUpdateRevisionAsync()
    {
        try
        {
            using var request = CreateGitHubRequest(UpdateCommitUrl);
            using var response = await UpdateHttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return (
                document.RootElement.GetProperty("sha").GetString() ?? "",
                document.RootElement.GetProperty("commit").GetProperty("message").GetString() ?? T("updateFound"));
        }
        catch
        {
            var gitRemoteSha = await TryGetRemoteGitShaAsync();
            if (!string.IsNullOrWhiteSpace(gitRemoteSha))
            {
                return (gitRemoteSha, T("updateFound"));
            }

            throw;
        }
    }

    private static async Task<string?> TryGetRemoteGitShaAsync()
    {
        var root = TryFindGitRoot();
        if (root is null)
        {
            return null;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = @"C:\Program Files\Git\cmd\git.exe",
                Arguments = $"ls-remote origin refs/heads/{UpdateBranch}",
                WorkingDirectory = root.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            return output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetLocalGitShaAsync()
    {
        var directory = TryFindGitRoot();
        if (directory is null)
        {
            return Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => x.Key == "GitCommit")
                ?.Value;
        }

        var gitPath = Path.Combine(directory.FullName, ".git");
        if (Directory.Exists(gitPath))
        {
            return await ReadGitHeadAsync(gitPath);
        }

        if (File.Exists(gitPath))
        {
            var content = await File.ReadAllTextAsync(gitPath);
            const string gitDirPrefix = "gitdir:";
            if (content.TrimStart().StartsWith(gitDirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var relativeGitDir = content[(content.IndexOf(gitDirPrefix, StringComparison.OrdinalIgnoreCase) + gitDirPrefix.Length)..].Trim();
                var fullGitDir = Path.GetFullPath(relativeGitDir, directory.FullName);
                return await ReadGitHeadAsync(fullGitDir);
            }
        }

        return null;
    }

    private static DirectoryInfo? TryFindGitRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static async Task<string?> ReadGitHeadAsync(string gitDirectory)
    {
        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        var head = (await File.ReadAllTextAsync(headPath)).Trim();
        const string refPrefix = "ref:";
        if (!head.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return head;
        }

        var reference = head[refPrefix.Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
        var refPath = Path.Combine(gitDirectory, reference);
        if (File.Exists(refPath))
        {
            return (await File.ReadAllTextAsync(refPath)).Trim();
        }

        var packedRefsPath = Path.Combine(gitDirectory, "packed-refs");
        if (!File.Exists(packedRefsPath))
        {
            return null;
        }

        var normalizedReference = reference.Replace(Path.DirectorySeparatorChar, '/');
        foreach (var line in await File.ReadAllLinesAsync(packedRefsPath))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1], normalizedReference, StringComparison.Ordinal))
            {
                return parts[0];
            }
        }

        return null;
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "unknown" : sha[..Math.Min(7, sha.Length)];

    private void ApplyThemeSetting(string theme)
    {
        var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        if (dark)
        {
            SetBrush("AppBackgroundBrush", Color.FromArgb(255, 20, 22, 20));
            SetBrush("PanelBrush", Color.FromArgb(255, 31, 34, 30));
            SetBrush("PanelAltBrush", Color.FromArgb(255, 38, 42, 37));
            SetBrush("LineBrush", Color.FromArgb(255, 60, 66, 58));
            SetBrush("MutedTextBrush", Color.FromArgb(255, 178, 185, 174));
            SetBrush("InkBrush", Color.FromArgb(255, 245, 245, 238));
            SetBrush("InputBrush", Color.FromArgb(255, 13, 16, 14));
            SetBrush("InputHoverBrush", Color.FromArgb(255, 18, 22, 19));
            SetBrush("InputPressedBrush", Color.FromArgb(255, 9, 11, 10));
            SetBrush("InputTextBrush", Color.FromArgb(255, 255, 255, 255));
            SetBrush("InputPlaceholderBrush", Color.FromArgb(255, 142, 151, 140));
            SetBrush("AccentSoftBrush", Color.FromArgb(255, 32, 59, 48));
            SetBrush("AccentBrush", Color.FromArgb(255, 79, 167, 127));
            SetBrush("DangerBrush", Color.FromArgb(255, 196, 93, 85));
            Background = (Brush)Resources["AppBackgroundBrush"];
            return;
        }

        SetBrush("AppBackgroundBrush", Color.FromArgb(255, 238, 248, 247));
        SetBrush("PanelBrush", Color.FromArgb(255, 247, 252, 251));
        SetBrush("PanelAltBrush", Color.FromArgb(255, 255, 255, 255));
        SetBrush("LineBrush", Color.FromArgb(255, 214, 236, 234));
        SetBrush("MutedTextBrush", Color.FromArgb(255, 96, 115, 112));
        SetBrush("InkBrush", Color.FromArgb(255, 24, 51, 49));
        SetBrush("InputBrush", Color.FromArgb(255, 255, 255, 255));
        SetBrush("InputHoverBrush", Color.FromArgb(255, 242, 251, 250));
        SetBrush("InputPressedBrush", Color.FromArgb(255, 230, 247, 245));
        SetBrush("InputTextBrush", Color.FromArgb(255, 24, 51, 49));
        SetBrush("InputPlaceholderBrush", Color.FromArgb(255, 109, 133, 130));
        SetBrush("AccentSoftBrush", Color.FromArgb(255, 216, 244, 241));
        SetBrush("AccentBrush", Color.FromArgb(255, 14, 159, 154));
        SetBrush("DangerBrush", Color.FromArgb(255, 178, 74, 67));
        Background = (Brush)Resources["AppBackgroundBrush"];
    }

    private void ApplyLanguageSetting(string language)
    {
        var normalizedLanguage = NormalizeAppLanguage(language);
        SetSetting(AppLanguageSettingKey, normalizedLanguage);
        var cultureLanguage = CultureLanguage(normalizedLanguage);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureLanguage);

        AuthSubtitleText.Text = T("authSubtitle");
        EmailLabel.Text = T("email");
        PasswordLabel.Text = T("password");
        ResetTokenLabel.Text = T("resetToken");
        EmailBox.PlaceholderText = T("emailPlaceholder");
        PasswordBox.PlaceholderText = T("passwordPlaceholder");
        TokenBox.PlaceholderText = T("tokenPlaceholder");
        LoginButtonText.Text = T("login");
        RegisterButtonText.Text = T("register");
        ForgotButtonText.Text = T("forgot");
        ResetButtonText.Text = T("reset");

        SyncButtonText.Text = T("sync");
        SettingsButtonText.Text = T("settings");
        LogoutButtonText.Text = T("logout");
        ActivityTitleText.Text = T("activity");
        SyncMetricTitleText.Text = T("syncShort");
        MediaMetricTitleText.Text = T("media");
        TranscriptMetricTitleText.Text = T("transcript");
        AllNotesTitleText.Text = T("allNotes");
        AllNotesSubtitleText.Text = T("allNotesSubtitle");
        if (MediaStateText.Text is "None" or "Нет")
        {
            MediaStateText.Text = T("none");
        }

        TimelineTitleText.Text = T("dayTimeline");
        TimelineSubtitleText.Text = T("timelineSubtitle");
        ApplyDateSelectorLanguage(cultureLanguage);
        DayPickerHeaderText.Text = T("day");
        SearchBox.Header = T("search");
        SearchBox.PlaceholderText = T("searchPlaceholder");

        NotePanelTitleText.Text = T("note");
        NotePanelSubtitleText.Text = T("noteSubtitle");
        StartTimePicker.Language = cultureLanguage;
        EndTimePicker.Language = cultureLanguage;
        StartTimePicker.Header = T("start");
        EndTimePicker.Header = T("end");
        VoiceMediaTitleText.Text = T("voiceMedia");
        VoiceMediaSubtitleText.Text = T("voiceMediaSubtitle");
        TranscriptionLanguageSummaryText.Text = $"{T("transcriptionLanguage")}: {FormatTranscriptionLanguage(GetSelectedLanguage())}";
        SetRecordButtonContent(_isRecording ? Symbol.Stop : Symbol.Microphone, _isRecording ? T("stopRecording") : T("recordAudio"));
        AddPhotoButtonText.Text = T("addPhoto");
        PlayAudioButtonText.Text = T("playAudio");
        StopAudioButtonText.Text = T("stop");
        TranscriptBox.PlaceholderText = T("transcriptPlaceholder");
        TitleBox.Header = T("title");
        TitleBox.PlaceholderText = T("titlePlaceholder");
        BodyBox.Header = T("text");
        BodyBox.PlaceholderText = T("bodyPlaceholder");
        BoldMenuItem.Text = T("formatBold");
        ItalicMenuItem.Text = T("formatItalic");
        HeadingMenuItem.Text = T("formatHeading");
        BulletedListMenuItem.Text = T("formatBulletedList");
        NumberedListMenuItem.Text = T("formatNumberedList");
        ChecklistMenuItem.Text = T("formatChecklist");
        QuoteMenuItem.Text = T("formatQuote");
        CodeMenuItem.Text = T("formatCode");
        LinkMenuItem.Text = T("formatLink");
        TableMenuItem.Text = T("formatTable");
        NewNoteButtonText.Text = T("newNote");
        SaveNoteButtonText.Text = T("saveNote");
        DeleteNoteButtonText.Text = T("delete");

        if (_selectedAudioAttachment is null)
        {
            AudioStatusText.Text = T("noAudioAttached");
        }

        if (PhotosPanel.Children.Count == 0)
        {
            PhotoStatusText.Text = T("noPhotosYet");
        }

        if (_selectedNoteId is null)
        {
            SetTranscriptState((LocalNote?)null);
        }
    }

    private void SetBrush(string key, Color color)
    {
        if (Resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private int GetDefaultDurationMinutes()
    {
        var raw = GetStringSetting(DefaultDurationSettingKey, "60");
        return int.TryParse(raw, out var minutes) && minutes > 0 ? minutes : 60;
    }

    private string FormatTranscriptionLanguage(string language)
    {
        return NormalizeLanguage(language) switch
        {
            "ru" => T("languageRussian"),
            "uk" => T("languageUkrainian"),
            "en" => T("languageEnglish"),
            _ => T("languageAuto")
        };
    }

    private static string NormalizeLanguage(string language)
    {
        return language.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : language.Equals("uk", StringComparison.OrdinalIgnoreCase)
                ? "uk"
                : language.Equals("en", StringComparison.OrdinalIgnoreCase)
                    ? "en"
                    : "auto";
    }

    private static string NormalizeAppLanguage(string language)
    {
        return language.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
    }

    private static string CultureLanguage(string language)
    {
        return NormalizeAppLanguage(language) switch
        {
            "ru" => "ru-RU",
            _ => "en-US"
        };
    }

    private void ApplyDateSelectorLanguage(string cultureLanguage)
    {
        MonthBox.Language = cultureLanguage;
        DayBox.Language = cultureLanguage;
        YearBox.Language = cultureLanguage;
        PopulateDateSelector(cultureLanguage);
    }

    private void SetSelectedDate(DateOnly date, bool reload)
    {
        _selectedDate = date;
        if (reload)
        {
            PopulateDateSelector(CultureLanguage(GetStringSetting(AppLanguageSettingKey, "en")));
        }
    }

    private void PopulateDateSelector(string cultureLanguage)
    {
        if (MonthBox is null || DayBox is null || YearBox is null)
        {
            return;
        }

        _isUpdatingDateSelector = true;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureLanguage);

            MonthBox.Items.Clear();
            for (var month = 1; month <= 12; month++)
            {
                MonthBox.Items.Add(new ComboBoxItem
                {
                    Content = CapitalizeMonth(culture.DateTimeFormat.GetMonthName(month), culture),
                    Tag = month
                });
            }

            var firstYear = Math.Min(DateTime.Now.Year, _selectedDate.Year) - 5;
            var lastYear = Math.Max(DateTime.Now.Year, _selectedDate.Year) + 5;
            YearBox.Items.Clear();
            for (var year = firstYear; year <= lastYear; year++)
            {
                YearBox.Items.Add(new ComboBoxItem
                {
                    Content = year.ToString(CultureInfo.InvariantCulture),
                    Tag = year
                });
            }

            PopulateDayBox(_selectedDate.Year, _selectedDate.Month, _selectedDate.Day);
            SelectComboBoxValue(MonthBox, _selectedDate.Month.ToString(CultureInfo.InvariantCulture));
            SelectComboBoxValue(YearBox, _selectedDate.Year.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            _isUpdatingDateSelector = false;
        }
    }

    private void PopulateDayBox(int year, int month, int selectedDay)
    {
        DayBox.Items.Clear();
        var days = DateTime.DaysInMonth(year, month);
        for (var day = 1; day <= days; day++)
        {
            DayBox.Items.Add(new ComboBoxItem
            {
                Content = day.ToString(CultureInfo.InvariantCulture),
                Tag = day
            });
        }

        SelectComboBoxValue(DayBox, Math.Clamp(selectedDay, 1, days).ToString(CultureInfo.InvariantCulture));
    }

    private static int SelectedInt(ComboBox box, int fallback)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            var raw = (item.Tag ?? item.Content)?.ToString();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string CapitalizeMonth(string month, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(month) ? month : culture.TextInfo.ToTitleCase(month);
    }

    private string T(string key)
    {
        var language = NormalizeAppLanguage(GetStringSetting(AppLanguageSettingKey, "en"));
        return language switch
        {
            "ru" => key switch
            {
                "appLanguage" => "Язык приложения",
                "theme" => "Тема",
                "themeLight" => "Светлая",
                "themeDark" => "Тёмная",
                "defaultNoteLength" => "Длина новой заметки",
                "duration30" => "30 минут",
                "duration60" => "1 час",
                "duration90" => "1.5 часа",
                "duration120" => "2 часа",
                "transcriptionLanguage" => "Язык транскрипции",
                "languageAuto" => "Авто",
                "languageRussian" => "Русский",
                "languageUkrainian" => "Украинский",
                "languageEnglish" => "Английский",
                "autoSyncMedia" => "Синхронизировать после записи или фото",
                "updates" => "\u041e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u044f",
                "checkUpdates" => "\u041f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c \u043e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u044f",
                "installUpdate" => "\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u043e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u0435",
                "updateNotChecked" => "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0435\u0449\u0451 \u043d\u0435 \u0437\u0430\u043f\u0443\u0441\u043a\u0430\u043b\u0430\u0441\u044c.",
                "checkingUpdates" => "\u041f\u0440\u043e\u0432\u0435\u0440\u044f\u044e GitHub...",
                "downloadingUpdate" => "\u0421\u043a\u0430\u0447\u0438\u0432\u0430\u044e \u043e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u0435...",
                "installerStarted" => "\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u0449\u0438\u043a \u0437\u0430\u043f\u0443\u0449\u0435\u043d. \u041f\u0440\u0438\u043b\u043e\u0436\u0435\u043d\u0438\u0435 \u0441\u0435\u0439\u0447\u0430\u0441 \u0437\u0430\u043a\u0440\u043e\u0435\u0442\u0441\u044f.",
                "updateInstallFailed" => "\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u043e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u0435",
                "installerAssetMissing" => "\u0412 GitHub Release \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d NotesMuchachosSetup.exe.",
                "latestGithubVersion" => "\u041f\u043e\u0441\u043b\u0435\u0434\u043d\u044f\u044f \u0432\u0435\u0440\u0441\u0438\u044f \u043d\u0430 GitHub",
                "currentBuildUnknown" => "\u0422\u0435\u043a\u0443\u0449\u0438\u0439 \u043a\u043e\u043c\u043c\u0438\u0442 \u0441\u0431\u043e\u0440\u043a\u0438 \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d.",
                "appUpToDate" => "\u0412\u0435\u0440\u0441\u0438\u044f \u0430\u043a\u0442\u0443\u0430\u043b\u044c\u043d\u0430:",
                "updateAvailable" => "\u0415\u0441\u0442\u044c \u043e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u0435:",
                "updateFound" => "\u041d\u0430 GitHub \u043d\u0430\u0439\u0434\u0435\u043d\u0430 \u043d\u043e\u0432\u0430\u044f \u0432\u0435\u0440\u0441\u0438\u044f.",
                "updateCheckFailed" => "\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c \u043e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u044f",
                "dateChangeFailed" => "\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u0441\u043c\u0435\u043d\u0438\u0442\u044c \u0434\u0435\u043d\u044c:",
                "settings" => "Настройки",
                "save" => "Сохранить",
                "cancel" => "Отмена",
                "settingsSaved" => "Настройки сохранены.",
                "authSubtitle" => "Войдите, чтобы открыть заметки, календарь и голосовые расшифровки.",
                "email" => "Email",
                "password" => "Пароль",
                "resetToken" => "Токен сброса",
                "emailPlaceholder" => "you@example.com",
                "passwordPlaceholder" => "Минимум 8 символов",
                "tokenPlaceholder" => "Нужен только после Forgot",
                "login" => "Войти",
                "register" => "Регистрация",
                "forgot" => "Забыли пароль",
                "reset" => "Сбросить",
                "sync" => "Синхронизация",
                "syncShort" => "Синхр.",
                "logout" => "Выйти",
                "activity" => "Активность",
                "media" => "Медиа",
                "transcript" => "Транскрипт",
                "allNotes" => "Все заметки",
                "allNotesSubtitle" => "Откройте любую заметку как полотно",
                "dayTimeline" => "День по часам",
                "timelineSubtitle" => "Один день, один час за раз",
                "day" => "День",
                "search" => "Поиск",
                "searchPlaceholder" => "Поиск по заметкам или транскрипту",
                "note" => "Заметка",
                "noteSubtitle" => "Пишите, добавляйте фото или записывайте аудио",
                "start" => "Начало",
                "end" => "Конец",
                "voiceMedia" => "Голос и медиа",
                "voiceMediaSubtitle" => "Запишите аудио или прикрепите фото.",
                "recordAudio" => "Записать аудио",
                "stopRecording" => "Остановить запись",
                "addPhoto" => "Добавить фото",
                "playAudio" => "Прослушать",
                "stop" => "Стоп",
                "transcriptPlaceholder" => "Транскрипт появится после синхронизации",
                "title" => "Название",
                "titlePlaceholder" => "Встреча, идея, звонок...",
                "text" => "Текст",
                "bodyPlaceholder" => "Начните писать...",
                "newNote" => "Новая",
                "saveNote" => "Сохранить",
                "delete" => "Удалить",
                "noAudioAttached" => "Аудио не прикреплено",
                "readyToPlayAudio" => "Аудио готово к прослушиванию",
                "playingAudio" => "Аудио воспроизводится",
                "noPhotosYet" => "Фото пока нет",
                "noTranscriptYet" => "Транскрипта пока нет",
                "noAudio" => "Нет аудио",
                "transcriptQueued" => "Транскрипция в очереди",
                "transcriptionInProgress" => "Транскрипция выполняется",
                "transcriptReady" => "Транскрипт готов",
                "transcriptionFailed" => "Транскрипция не удалась",
                "queued" => "В очереди",
                "processing" => "В работе",
                "ready" => "Готово",
                "failed" => "Ошибка",
                "none" => "Нет",
                "photo" => "фото",
                "audio" => "аудио",
                "noNotesYet" => "Заметок пока нет.",
                "untitledNote" => "Без названия",
                "noTextYet" => "Текста пока нет",
                "openedNote" => "Открыта заметка",
                "boldTextSample" => "жирный текст",
                "italicTextSample" => "курсив",
                "codeTextSample" => "код",
                "linkTextSample" => "ссылка",
                "listItemSample" => "пункт",
                "tableColumn" => "Колонка",
                "tableCell" => "Ячейка",
                "formatBold" => "Жирный",
                "formatItalic" => "Курсив",
                "formatHeading" => "Заголовок",
                "formatBulletedList" => "Маркированный список",
                "formatNumberedList" => "Нумерованный список",
                "formatChecklist" => "Чеклист",
                "formatQuote" => "Цитата",
                "formatCode" => "Код",
                "formatLink" => "Ссылка",
                "formatTable" => "Таблица",
                "recordingStarted" => "Идёт запись аудио. Нажмите остановку, когда закончите.",
                "checkingSession" => "Проверяю сохранённый вход...",
                "welcomeBack" => "С возвращением. Это устройство запомнено.",
                "registerOnce" => "Зарегистрируйтесь один раз, затем войдите. Это устройство будет запомнено.",
                "accountCreated" => "Аккаунт создан. Нажмите Войти, чтобы открыть приложение.",
                "signedInRemembered" => "Вы вошли. Это устройство будет запомнено.",
                "loggedOut" => "Вы вышли. Войдите снова, чтобы запомнить это устройство.",
                "resetTokenCreated" => "Токен сброса пароля создан для разработки.",
                "passwordChanged" => "Пароль изменён. Теперь можно войти.",
                "offline" => "Офлайн",
                _ => key
            },
            _ => key switch
            {
                "appLanguage" => "App language",
                "theme" => "Theme",
                "themeLight" => "Light",
                "themeDark" => "Dark",
                "defaultNoteLength" => "Default note length",
                "duration30" => "30 minutes",
                "duration60" => "1 hour",
                "duration90" => "1.5 hours",
                "duration120" => "2 hours",
                "transcriptionLanguage" => "Transcription language",
                "languageAuto" => "Auto",
                "languageRussian" => "Russian",
                "languageUkrainian" => "Ukrainian",
                "languageEnglish" => "English",
                "autoSyncMedia" => "Auto sync after recording or photo",
                "updates" => "Updates",
                "checkUpdates" => "Check GitHub updates",
                "installUpdate" => "Install update",
                "updateNotChecked" => "Update check has not run yet.",
                "checkingUpdates" => "Checking GitHub...",
                "downloadingUpdate" => "Downloading update...",
                "installerStarted" => "Installer started. The app will close now.",
                "updateInstallFailed" => "Could not install update",
                "installerAssetMissing" => "NotesMuchachosSetup.exe was not found in the GitHub Release.",
                "latestGithubVersion" => "Latest GitHub version",
                "currentBuildUnknown" => "Current build commit was not found.",
                "appUpToDate" => "App is up to date:",
                "updateAvailable" => "Update available:",
                "updateFound" => "A newer version was found on GitHub.",
                "updateCheckFailed" => "Could not check updates",
                "dateChangeFailed" => "Could not change day:",
                "settings" => "Settings",
                "save" => "Save",
                "cancel" => "Cancel",
                "settingsSaved" => "Settings saved.",
                "authSubtitle" => "Sign in to open your notes, calendar and voice transcripts.",
                "email" => "Email",
                "password" => "Password",
                "resetToken" => "Reset token",
                "emailPlaceholder" => "you@example.com",
                "passwordPlaceholder" => "Minimum 8 characters",
                "tokenPlaceholder" => "Only needed after Forgot",
                "login" => "Login",
                "register" => "Register",
                "forgot" => "Forgot",
                "reset" => "Reset",
                "sync" => "Sync",
                "syncShort" => "Sync",
                "logout" => "Logout",
                "activity" => "Activity",
                "media" => "Media",
                "transcript" => "Transcript",
                "allNotes" => "All notes",
                "allNotesSubtitle" => "Open any note as a full canvas",
                "dayTimeline" => "Day timeline",
                "timelineSubtitle" => "Focus on one day, one hour at a time",
                "day" => "Day",
                "search" => "Search",
                "searchPlaceholder" => "Search notes or transcript",
                "note" => "Note",
                "noteSubtitle" => "Write, attach photos, or record audio",
                "start" => "Start",
                "end" => "End",
                "voiceMedia" => "Voice & media",
                "voiceMediaSubtitle" => "Record audio or attach photos.",
                "recordAudio" => "Record audio",
                "stopRecording" => "Stop recording",
                "addPhoto" => "Add photo",
                "playAudio" => "Play audio",
                "stop" => "Stop",
                "transcriptPlaceholder" => "Transcript will appear here after sync",
                "title" => "Title",
                "titlePlaceholder" => "Meeting, idea, call...",
                "text" => "Text",
                "bodyPlaceholder" => "Start typing...",
                "newNote" => "New",
                "saveNote" => "Save note",
                "delete" => "Delete",
                "noAudioAttached" => "No audio attached",
                "readyToPlayAudio" => "Ready to play latest audio",
                "playingAudio" => "Playing audio",
                "noPhotosYet" => "No photos yet",
                "noTranscriptYet" => "No transcript yet",
                "noAudio" => "No audio",
                "transcriptQueued" => "Transcript queued",
                "transcriptionInProgress" => "Transcription in progress",
                "transcriptReady" => "Transcript ready",
                "transcriptionFailed" => "Transcription failed",
                "queued" => "Queued",
                "processing" => "Processing",
                "ready" => "Ready",
                "failed" => "Failed",
                "none" => "None",
                "photo" => "photo",
                "audio" => "audio",
                "noNotesYet" => "No notes yet.",
                "untitledNote" => "Untitled note",
                "noTextYet" => "No text yet",
                "openedNote" => "Opened",
                "boldTextSample" => "bold text",
                "italicTextSample" => "italic text",
                "codeTextSample" => "code",
                "linkTextSample" => "link",
                "listItemSample" => "item",
                "tableColumn" => "Column",
                "tableCell" => "Cell",
                "formatBold" => "Bold",
                "formatItalic" => "Italic",
                "formatHeading" => "Heading",
                "formatBulletedList" => "Bulleted list",
                "formatNumberedList" => "Numbered list",
                "formatChecklist" => "Checklist",
                "formatQuote" => "Quote",
                "formatCode" => "Code",
                "formatLink" => "Link",
                "formatTable" => "Table",
                "recordingStarted" => "Recording audio. Press Stop recording when done.",
                "checkingSession" => "Checking saved session...",
                "welcomeBack" => "Welcome back. Your device is remembered.",
                "registerOnce" => "Register once, then login. This device will be remembered.",
                "accountCreated" => "Account created. Press Login to open the app.",
                "signedInRemembered" => "Signed in. This device will be remembered.",
                "loggedOut" => "Logged out. Login again to remember this device.",
                "resetTokenCreated" => "Password reset token created for development.",
                "passwordChanged" => "Password changed. You can log in now.",
                "offline" => "Offline",
                _ => key
            }
        };
    }

    private string GetSelectedLanguage()
    {
        return GetStringSetting(TranscriptionLanguageSettingKey, "auto");
    }

    private void SelectComboBoxValue(ComboBox box, string value)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is not ComboBoxItem item)
            {
                continue;
            }

            var itemValue = (item.Tag ?? item.Content)?.ToString();
            if (string.Equals(itemValue, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        box.SelectedIndex = box.Items.Count > 0 ? 0 : -1;
    }

    private static string SelectedTag(ComboBox box, string fallback)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            return (item.Tag ?? item.Content)?.ToString() ?? fallback;
        }

        return fallback;
    }

    private bool ConfigureApiClientFromSettings(bool resetSession)
    {
        var baseUrl = ResolveApiBaseUrl();
        return _api.SetBaseUrl(baseUrl, resetSession);
    }

    private string ResolveApiBaseUrl()
    {
        var managedUrl = NormalizeApiBaseUrl(
            Environment.GetEnvironmentVariable("PROJECTCAL_API_URL")
            ?? Environment.GetEnvironmentVariable("PROJECTCAL_API_URL", EnvironmentVariableTarget.User)
            ?? ManagedCloudApiUrl);

        return string.IsNullOrWhiteSpace(managedUrl) ? LocalApiUrl : managedUrl;
    }

    private static string NormalizeApiBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "";
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string GetStringSetting(string key, string fallback)
    {
        return ClientAppData.GetString(key) ?? fallback;
    }

    private static bool GetBoolSetting(string key, bool fallback)
    {
        return ClientAppData.GetBool(key) ?? fallback;
    }

    private static void SetSetting(string key, object value)
    {
        switch (value)
        {
            case bool boolValue:
                ClientAppData.Set(key, boolValue);
                break;
            default:
                ClientAppData.Set(key, value.ToString() ?? "");
                break;
        }
    }

    private void StopAudioPlayback()
    {
        _audioPlayer?.Pause();
        if (_audioPlayer is not null)
        {
            _audioPlayer.Source = null;
        }

        _loadedAudioAttachmentId = null;
        AudioPlayerElement.Visibility = Visibility.Collapsed;
    }

    private MediaPlayer CreateAudioPlayer()
    {
        var player = new MediaPlayer();
        player.MediaEnded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AudioStatusText.Text = _selectedAudioAttachment is null ? T("noAudioAttached") : T("readyToPlayAudio");
            });
        };

        return player;
    }

    private void RenderPhotos(IReadOnlyList<LocalAttachment> attachments)
    {
        var photos = attachments
            .Where(x => x.Type == AttachmentType.Photo && File.Exists(x.LocalPath))
            .ToArray();

        PhotosPanel.Children.Clear();
        PhotoStatusText.Text = photos.Length == 0
            ? T("noPhotosYet")
            : $"{photos.Length} {T("photo")} attached. Click a photo to preview.";

        foreach (var photo in photos)
        {
            var image = new Image
            {
                Width = 112,
                Height = 96,
                Stretch = Stretch.UniformToFill,
                Source = new BitmapImage(new Uri(photo.LocalPath))
            };

            var frame = new Border
            {
                Width = 114,
                Height = 98,
                Padding = new Thickness(1),
                Background = (Brush)Resources["PanelAltBrush"],
                BorderBrush = (Brush)Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = image
            };

            var label = new TextBlock
            {
                Text = photo.FileName,
                MaxWidth = 116,
                Foreground = (Brush)Resources["MutedTextBrush"],
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var content = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    frame,
                    label
                }
            };

            var thumbnail = new Button
            {
                Width = 124,
                Height = 130,
                Padding = new Thickness(4),
                Background = (Brush)Resources["PanelAltBrush"],
                BorderBrush = (Brush)Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Tag = photo,
                Content = content
            };
            thumbnail.Click += PhotoThumbnail_Click;

            ToolTipService.SetToolTip(thumbnail, $"Preview {photo.FileName}");
            PhotosPanel.Children.Add(thumbnail);
        }
    }

    private async void PhotoThumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LocalAttachment photo })
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            if (!File.Exists(photo.LocalPath))
            {
                StatusBox.Text = "Photo file is missing from local storage.";
                return;
            }

            await ShowPhotoAsync(photo);
        });
    }

    private async Task ShowPhotoAsync(LocalAttachment photo)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri(photo.LocalPath)),
            Stretch = Stretch.Uniform,
            MaxWidth = 980,
            MaxHeight = 680
        };

        var viewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.5f,
            MaxZoomFactor = 4f,
            Content = image
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = photo.FileName,
            Content = viewer,
            PrimaryButtonText = "Open file",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var file = await StorageFile.GetFileFromPathAsync(photo.LocalPath);
            await Launcher.LaunchFileAsync(file);
        };

        await dialog.ShowAsync();
    }

    private static void InitializePicker(object picker)
    {
        var window = App.CurrentWindow;
        if (window is null)
        {
            return;
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (UnauthorizedAccessException)
        {
            StatusBox.Text = "Microphone access is blocked. Enable microphone permission for NotesMuchachos in Windows Settings.";
        }
        catch (Exception ex)
        {
            StatusBox.Text = ex.Message;
            SyncStateText.Text = "Issue";
            ClearMediaAction();
        }
    }

    private async Task RunAuthAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AuthStatusBox.Text = ex.Message;
        }
    }

    private void OpenAppShell(string status)
    {
        AuthScreen.Visibility = Visibility.Collapsed;
        AppShell.Visibility = Visibility.Visible;
        UserLabel.Text = _api.CurrentUser?.Email ?? "Calendar-first notes";
        StatusBox.Text = status;
        SyncStateText.Text = _api.IsSignedIn ? T("ready") : T("offline");
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void ReturnToAuthScreen(string status)
    {
        _selectedNoteId = null;
        _resizeHandlesNoteId = null;
        ClearMediaState();
        AuthScreen.Visibility = Visibility.Visible;
        AppShell.Visibility = Visibility.Collapsed;
        AuthStatusBox.Text = status;
    }
}
