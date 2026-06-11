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
    private const string DefaultDurationSettingKey = "settings_default_duration";
    private const string TranscriptionLanguageSettingKey = "settings_transcription_language";
    private const string AutoSyncMediaSettingKey = "settings_auto_sync_media";

    private readonly LocalNoteStore _store = new();
    private readonly ProjectCalApiClient _api = new();
    private DateTimeOffset? _lastSyncAt;
    private Guid? _selectedNoteId;
    private MediaCapture? _mediaCapture;
    private MediaPlayer? _audioPlayer;
    private StorageFile? _recordingFile;
    private LocalAttachment? _selectedAudioAttachment;
    private LocalNote? _resizingNote;
    private FrameworkElement? _resizeVisual;
    private ResizeEdge _resizeEdge;
    private int _resizeOriginalStartMinutes;
    private int _resizeOriginalEndMinutes;
    private double _resizeStartY;
    private bool _isRecording;

    private const double HourHeight = 118;
    private const double MinuteHeight = HourHeight / 60.0;

    private enum ResizeEdge
    {
        Top,
        Bottom
    }

    private sealed record ResizeContext(LocalNote Note, FrameworkElement Host, ResizeEdge Edge);

    public MainPage()
    {
        InitializeComponent();
        ApplyThemeSetting(GetStringSetting(ThemeSettingKey, "Light"));
        _audioPlayer = CreateAudioPlayer();
        AudioPlayerElement.SetMediaPlayer(_audioPlayer);
        DayPicker.Date = DateTimeOffset.Now;
        StartTimePicker.Time = TimeSpan.FromHours(DateTimeOffset.Now.Hour);
        EndTimePicker.Time = TimeOnly.FromTimeSpan(StartTimePicker.Time).AddMinutes(GetDefaultDurationMinutes()).ToTimeSpan();
        SetLanguageBoxValue(GetStringSetting(TranscriptionLanguageSettingKey, "auto"));
        Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await _store.InitializeAsync();
        AuthStatusBox.Text = "Checking saved session...";
        if (await _api.TryRestoreSessionAsync())
        {
            OpenAppShell("Welcome back. Your device is remembered.");
            await ReloadAsync();
            return;
        }

        AuthStatusBox.Text = "Register once, then login. This device will be remembered.";
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            var result = await _api.RegisterAsync(EmailBox.Text, PasswordBox.Password);
            TokenBox.Text = result.DevelopmentEmailToken ?? "";
            AuthStatusBox.Text = "Account created. Press Login to open the app.";
        });
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            await _api.LoginAsync(EmailBox.Text, PasswordBox.Password);
            OpenAppShell("Signed in. This device will be remembered.");
            await ReloadAsync();
        });
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _api.Logout();
        _selectedNoteId = null;
        ClearMediaState();
        AuthScreen.Visibility = Visibility.Visible;
        AppShell.Visibility = Visibility.Collapsed;
        AuthStatusBox.Text = "Logged out. Login again to remember this device.";
        await ReloadAsync();
    }

    private async void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            TokenBox.Text = await _api.ForgotPasswordAsync(EmailBox.Text) ?? "";
            AuthStatusBox.Text = "Password reset token created for development.";
        });
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        await RunAuthAsync(async () =>
        {
            await _api.ResetPasswordAsync(EmailBox.Text, TokenBox.Text, PasswordBox.Password);
            AuthStatusBox.Text = "Password changed. You can log in now.";
        });
    }

    private async void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            var note = await _store.UpsertNoteAsync(
                _selectedNoteId,
                TitleBox.Text,
                BodyBox.Text,
                SelectedDate(),
                TimeOnly.FromTimeSpan(StartTimePicker.Time),
                TimeOnly.FromTimeSpan(EndTimePicker.Time));

            _selectedNoteId = note.Id;
            SetTranscriptState(note);
            StatusBox.Text = "Saved locally.";
            SyncStateText.Text = "Local changes";
            await ReloadAsync();
        });
    }

    private async void NewNote_Click(object sender, RoutedEventArgs e)
    {
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
            TitleBox.Text = "";
            BodyBox.Text = "";
            ClearMediaState();
            SetTranscriptState(null);
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

                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("ProjectCalRecordings", CreationCollisionOption.OpenIfExists);
                _recordingFile = await folder.CreateFileAsync($"{Guid.NewGuid():N}.m4a", CreationCollisionOption.GenerateUniqueName);
                await _mediaCapture.StartRecordToStorageFileAsync(MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto), _recordingFile);
                _isRecording = true;
                SetRecordButtonContent(Symbol.Stop, "Stop recording");
                StatusBox.Text = "Recording audio. Press Stop recording when done.";
                MediaStateText.Text = "Recording";
                await ReloadAsync();
                return;
            }

            await _mediaCapture!.StopRecordAsync();
            _mediaCapture.Dispose();
            _mediaCapture = null;
            _isRecording = false;
            SetRecordButtonContent(Symbol.Microphone, "Record audio");

            var recordedFile = _recordingFile;
            _recordingFile = null;
            if (recordedFile is not null)
            {
                await _store.AddAttachmentAsync(_selectedNoteId!.Value, recordedFile, AttachmentType.Audio);
                await LoadSelectedMediaAsync(_selectedNoteId.Value);
            }

            StatusBox.Text = "Audio attached locally. Sync uploads it for transcription.";
            SyncStateText.Text = "Local changes";
            TranscriptStateText.Text = "Needs sync";
            if (_api.IsSignedIn && GetBoolSetting(AutoSyncMediaSettingKey, false))
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
        var themeBox = SettingComboBox("Theme", [
            ("Light", "Light"),
            ("Dark", "Dark")
        ], GetStringSetting(ThemeSettingKey, "Light"));

        var durationBox = SettingComboBox("Default note length", [
            ("30 minutes", "30"),
            ("1 hour", "60"),
            ("1.5 hours", "90"),
            ("2 hours", "120")
        ], GetDefaultDurationMinutes().ToString());

        var languageBox = SettingComboBox("Transcription language", [
            ("Auto", "auto"),
            ("Russian", "ru"),
            ("Ukrainian", "uk"),
            ("English", "en")
        ], GetStringSetting(TranscriptionLanguageSettingKey, "auto"));

        var autoSyncSwitch = new ToggleSwitch
        {
            Header = "Auto sync after recording or photo",
            IsOn = GetBoolSetting(AutoSyncMediaSettingKey, false)
        };

        var panel = new StackPanel
        {
            Spacing = 14,
            MinWidth = 360,
            Children =
            {
                themeBox,
                durationBox,
                languageBox,
                autoSyncSwitch
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var theme = SelectedTag(themeBox, "Light");
        var duration = SelectedTag(durationBox, "60");
        var language = SelectedTag(languageBox, "auto");

        SetSetting(ThemeSettingKey, theme);
        SetSetting(DefaultDurationSettingKey, duration);
        SetSetting(TranscriptionLanguageSettingKey, language);
        SetSetting(AutoSyncMediaSettingKey, autoSyncSwitch.IsOn);

        ApplyThemeSetting(theme);
        SetLanguageBoxValue(language);
        if (_selectedNoteId is null)
        {
            EndTimePicker.Time = TimeOnly.FromTimeSpan(StartTimePicker.Time).AddMinutes(GetDefaultDurationMinutes()).ToTimeSpan();
        }

        StatusBox.Text = "Settings saved.";
    }

    private async Task SyncNowAsync()
    {
        SyncStateText.Text = "Syncing";
        var dirtyNotes = await _store.GetDirtyNotesAsync();
        var mutations = dirtyNotes.Select(note => new SyncNoteMutation(
            note.DeletedAt is null ? SyncOperation.Upsert : SyncOperation.Delete,
            new UpsertNoteRequest(note.Id, note.Title, note.Body, note.Date, note.StartTime, note.EndTime, note.SyncVersion))).ToArray();

        var response = await _api.SyncAsync(new SyncRequest(_lastSyncAt, mutations));
        foreach (var note in response.Notes)
        {
            await _store.ApplyServerNoteAsync(note);
        }

        foreach (var transcript in response.Transcripts)
        {
            await _store.ApplyServerTranscriptAsync(transcript);
        }

        var uploadedMedia = 0;
        var skippedMedia = 0;
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
                await EnsureServerNoteForAttachmentAsync(attachment.NoteId);
                await _api.UploadAttachmentAsync(attachment, GetSelectedLanguage());
            }

            await _store.MarkAttachmentUploadedAsync(attachment.Id);
            uploadedMedia++;
        }

        _lastSyncAt = response.ServerTime;
        if (uploadedMedia > 0)
        {
            StatusBox.Text = $"Uploaded {uploadedMedia} media file(s). Waiting for transcription...";
            MediaStateText.Text = "Uploaded";
            TranscriptStateText.Text = "Processing";
            await PullTranscriptionUpdatesAsync();
            StatusBox.Text = skippedMedia > 0
                ? $"Synced. Uploaded {uploadedMedia} media file(s), skipped {skippedMedia} stale file(s)."
                : "Synced. Uploaded media and checked transcription.";
            SyncStateText.Text = "Done";
        }
        else
        {
            StatusBox.Text = skippedMedia > 0
                ? $"Synced {response.Notes.Count} notes. Skipped {skippedMedia} stale media file(s)."
                : $"Synced {response.Notes.Count} notes. Checked transcription updates.";
            SyncStateText.Text = "Done";
        }

        await RefreshSelectedNoteDetailsAsync();
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
            new UpsertNoteRequest(note.Id, note.Title, note.Body, note.Date, note.StartTime, note.EndTime, note.SyncVersion));

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

    private async void DayPicker_DateChanged(object sender, DatePickerValueChangedEventArgs e)
    {
        await ReloadAsync();
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
        if (notes.Count == 1 && (_selectedNoteId is null || notes.All(x => x.Id != _selectedNoteId)))
        {
            SelectNote(notes[0], "Selected the only note on this day.");
            await LoadSelectedMediaAsync(notes[0].Id);
        }

        HoursPanel.Children.Clear();
        HoursPanel.Children.Add(BuildDayTimeline(notes, attachmentSummaries));
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
                child.Width = Math.Max(120, args.NewSize.Width - 14);
            }
        };
        contentLayer.Children.Add(notesCanvas);

        foreach (var note in notes)
        {
            attachmentSummaries.TryGetValue(note.Id, out var attachmentSummary);
            var noteVisual = BuildTimelineNote(note, attachmentSummary, panelAltBrush, inkBrush, mutedTextBrush, accentBrush, accentSoftBrush, lineBrush);
            noteVisual.Width = Math.Max(120, notesCanvas.ActualWidth - 14);
            Canvas.SetLeft(noteVisual, 7);
            Canvas.SetTop(noteVisual, NoteStartMinute(note) * MinuteHeight);
            notesCanvas.Children.Add(noteVisual);
        }

        return root;
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
        var startMinute = NoteStartMinute(note);
        var endMinute = NoteEndMinute(note);
        var height = Math.Max(54, (endMinute - startMinute) * MinuteHeight);

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

        var copy = new StackPanel
        {
            Spacing = 4,
            Padding = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(copy, 1);
        cardGrid.Children.Add(copy);

        copy.Children.Add(new TextBlock
        {
            Text = $"{note.StartTime:HH:mm}-{(note.EndTime ?? note.StartTime.AddMinutes(GetDefaultDurationMinutes())):HH:mm}  {note.Title}",
            Foreground = inkBrush,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        copy.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Body) ? "No text yet" : note.Body,
            Foreground = mutedTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = Math.Max(1, (int)(height / 54))
        });

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
        host.Children.Add(noteButton);

        if (isSelected)
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
            emptySlot.DoubleTapped += EmptyHour_DoubleTapped;
            stack.Children.Add(emptySlot);
            return row;
        }

        foreach (var note in notes)
        {
            var isSelected = _selectedNoteId == note.Id;
            var durationMinutes = NoteDurationMinutes(note);
            var visualHeight = Math.Max(58, durationMinutes * 1.15);
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
                Text = string.IsNullOrWhiteSpace(note.Body) ? "No text yet" : note.Body,
                Foreground = mutedTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
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

            if (!isSelected)
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

        SelectNote(note, $"Selected {note.Title}.");
        await LoadSelectedMediaAsync(note.Id);
    }

    private void SelectNote(LocalNote note, string status)
    {
        _selectedNoteId = note.Id;
        TitleBox.Text = note.Title;
        BodyBox.Text = note.Body;
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
            StartTimePicker.Time = saved.StartTime.ToTimeSpan();
            EndTimePicker.Time = (saved.EndTime ?? saved.StartTime.AddMinutes(GetDefaultDurationMinutes())).ToTimeSpan();
            StatusBox.Text = $"Resized to {saved.StartTime:HH:mm}-{saved.EndTime:HH:mm}. Saved locally.";
            SyncStateText.Text = "Local changes";
            await ReloadAsync();
        });

        e.Handled = true;
    }

    private async void EmptyHour_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: int hour })
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var startTime = new TimeOnly(hour, 0);
            var note = await _store.UpsertNoteAsync(
                null,
                "Untitled note",
                "",
                SelectedDate(),
                startTime,
                startTime.AddMinutes(GetDefaultDurationMinutes()));

            ClearMediaState();
            SelectNote(note, $"Created note at {startTime:HH:mm}. Drag arrows to resize.");
            SyncStateText.Text = "Local changes";
            await ReloadAsync();
        });

        e.Handled = true;
    }

    private void StartNewNote(TimeOnly startTime)
    {
        _selectedNoteId = null;
        TitleBox.Text = "";
        BodyBox.Text = "";
        StartTimePicker.Time = startTime.ToTimeSpan();
        EndTimePicker.Time = startTime.AddMinutes(GetDefaultDurationMinutes()).ToTimeSpan();
        ClearMediaState();
        SetTranscriptState(null);
        StatusBox.Text = $"New separate note at {startTime:HH:mm}.";
    }

    private async Task EnsureCurrentNoteAsync(string fallbackTitle)
    {
        if (_selectedNoteId is not null)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? fallbackTitle : TitleBox.Text;
        var note = await _store.UpsertNoteAsync(
            null,
            title,
            BodyBox.Text,
            SelectedDate(),
            TimeOnly.FromTimeSpan(StartTimePicker.Time),
            TimeOnly.FromTimeSpan(EndTimePicker.Time));

        _selectedNoteId = note.Id;
        TitleBox.Text = note.Title;
    }

    private DateOnly SelectedDate() => DateOnly.FromDateTime(DayPicker.Date.DateTime);

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

    private async void PlayAudio_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            if (_selectedAudioAttachment is null)
            {
                StatusBox.Text = "This note has no local audio yet.";
                return;
            }

            if (!File.Exists(_selectedAudioAttachment.LocalPath))
            {
                StatusBox.Text = "Audio file is missing from local storage.";
                await LoadSelectedMediaAsync(_selectedAudioAttachment.NoteId);
                return;
            }

            var file = await StorageFile.GetFileFromPathAsync(_selectedAudioAttachment.LocalPath);
            _audioPlayer ??= CreateAudioPlayer();
            AudioPlayerElement.SetMediaPlayer(_audioPlayer);
            AudioPlayerElement.Visibility = Visibility.Visible;
            _audioPlayer.Source = MediaSource.CreateFromStorageFile(file);
            _audioPlayer.Play();
            AudioStatusText.Text = $"Playing {_selectedAudioAttachment.FileName}";
            StatusBox.Text = "Playing local audio. Use the player controls under the buttons.";
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
        AudioStatusText.Text = _selectedAudioAttachment is null ? "No audio attached" : $"Ready to play {_selectedAudioAttachment.FileName}";
        StatusBox.Text = "Audio stopped.";
    }

    private async Task LoadSelectedMediaAsync(Guid noteId)
    {
        StopAudioPlayback();
        var attachments = await _store.GetAttachmentsForNoteAsync(noteId);
        var audioAttachments = attachments
            .Where(x => x.Type == AttachmentType.Audio && File.Exists(x.LocalPath))
            .ToArray();
        _selectedAudioAttachment = audioAttachments.FirstOrDefault();

        var hasAudio = _selectedAudioAttachment is not null;
        PlayAudioButton.IsEnabled = hasAudio;
        StopAudioButton.IsEnabled = hasAudio;
        AudioPlayerElement.Visibility = Visibility.Collapsed;
        AudioStatusText.Text = hasAudio
            ? $"{audioAttachments.Length} audio attached. Ready to play latest."
            : "No audio attached";

        RenderPhotos(attachments);
        UpdateMediaIndicator(attachments);
    }

    private void ClearMediaState()
    {
        StopAudioPlayback();
        _selectedAudioAttachment = null;
        PlayAudioButton.IsEnabled = false;
        StopAudioButton.IsEnabled = false;
        AudioPlayerElement.Visibility = Visibility.Collapsed;
        AudioStatusText.Text = "No audio attached";
        PhotosPanel.Children.Clear();
        PhotoStatusText.Text = "No photos yet";
        MediaStateText.Text = "None";
    }

    private async Task RefreshSelectedNoteDetailsAsync()
    {
        if (_selectedNoteId is null)
        {
            SetTranscriptState(null);
            return;
        }

        var note = await _store.GetNoteByIdAsync(_selectedNoteId.Value);
        if (note is null)
        {
            SetTranscriptState(null);
            return;
        }

        TitleBox.Text = note.Title;
        BodyBox.Text = note.Body;
        SetTranscriptState(note);
    }

    private void SetTranscriptState(LocalNote? note)
    {
        if (note is null)
        {
            TranscriptStatusText.Text = "No transcript yet";
            TranscriptBox.Text = "";
            TranscriptStateText.Text = "No audio";
            return;
        }

        TranscriptStatusText.Text = note.TranscriptStatus switch
        {
            TranscriptStatus.Pending => "Transcript queued",
            TranscriptStatus.Processing => "Transcription in progress",
            TranscriptStatus.Done => "Transcript ready",
            TranscriptStatus.Failed => "Transcription failed",
            _ => "No transcript yet"
        };
        TranscriptBox.Text = note.TranscriptText ?? "";
        TranscriptStateText.Text = note.TranscriptStatus switch
        {
            TranscriptStatus.Pending => "Queued",
            TranscriptStatus.Processing => "Processing",
            TranscriptStatus.Done => "Ready",
            TranscriptStatus.Failed => "Failed",
            _ => "No audio"
        };
    }

    private void UpdateMediaIndicator(IReadOnlyList<LocalAttachment> attachments)
    {
        var photos = attachments.Count(x => x.Type == AttachmentType.Photo && File.Exists(x.LocalPath));
        var audio = attachments.Count(x => x.Type == AttachmentType.Audio && File.Exists(x.LocalPath));
        MediaStateText.Text = (photos, audio) switch
        {
            (0, 0) => "None",
            (0, _) => $"{audio} audio",
            (_, 0) => $"{photos} photo",
            _ => $"{photos} photo / {audio} audio"
        };
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

        SetBrush("AppBackgroundBrush", Color.FromArgb(255, 238, 241, 236));
        SetBrush("PanelBrush", Color.FromArgb(255, 247, 247, 242));
        SetBrush("PanelAltBrush", Color.FromArgb(255, 255, 255, 255));
        SetBrush("LineBrush", Color.FromArgb(255, 229, 226, 218));
        SetBrush("MutedTextBrush", Color.FromArgb(255, 111, 106, 98));
        SetBrush("InkBrush", Color.FromArgb(255, 33, 31, 27));
        SetBrush("InputBrush", Color.FromArgb(255, 20, 24, 22));
        SetBrush("InputHoverBrush", Color.FromArgb(255, 27, 33, 30));
        SetBrush("InputPressedBrush", Color.FromArgb(255, 16, 19, 17));
        SetBrush("InputTextBrush", Color.FromArgb(255, 255, 255, 255));
        SetBrush("InputPlaceholderBrush", Color.FromArgb(255, 174, 183, 177));
        SetBrush("AccentSoftBrush", Color.FromArgb(255, 220, 239, 230));
        SetBrush("AccentBrush", Color.FromArgb(255, 49, 122, 91));
        SetBrush("DangerBrush", Color.FromArgb(255, 178, 74, 67));
        Background = (Brush)Resources["AppBackgroundBrush"];
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

    private string GetSelectedLanguage()
    {
        return SelectedTag(LanguageBox, GetStringSetting(TranscriptionLanguageSettingKey, "auto"));
    }

    private void SetLanguageBoxValue(string language)
    {
        SelectComboBoxValue(LanguageBox, language);
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

    private static string GetStringSetting(string key, string fallback)
    {
        return ApplicationData.Current.LocalSettings.Values[key] as string ?? fallback;
    }

    private static bool GetBoolSetting(string key, bool fallback)
    {
        return ApplicationData.Current.LocalSettings.Values[key] is bool value ? value : fallback;
    }

    private static void SetSetting(string key, object value)
    {
        ApplicationData.Current.LocalSettings.Values[key] = value;
    }

    private void StopAudioPlayback()
    {
        _audioPlayer?.Pause();
        if (_audioPlayer is not null)
        {
            _audioPlayer.Source = null;
        }

        AudioPlayerElement.Visibility = Visibility.Collapsed;
    }

    private MediaPlayer CreateAudioPlayer()
    {
        var player = new MediaPlayer();
        player.MediaEnded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AudioStatusText.Text = _selectedAudioAttachment is null ? "No audio attached" : $"Ready to play {_selectedAudioAttachment.FileName}";
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
        PhotoStatusText.Text = photos.Length == 0 ? "No photos yet" : $"{photos.Length} photo{(photos.Length == 1 ? "" : "s")} attached";

        foreach (var photo in photos)
        {
            var image = new Image
            {
                Width = 86,
                Height = 86,
                Stretch = Stretch.UniformToFill,
                Source = new BitmapImage(new Uri(photo.LocalPath))
            };

            var frame = new Border
            {
                Width = 88,
                Height = 88,
                Padding = new Thickness(1),
                Background = (Brush)Resources["PanelAltBrush"],
                BorderBrush = (Brush)Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = image
            };

            var thumbnail = new Button
            {
                Width = 92,
                Height = 92,
                Padding = new Thickness(0),
                Background = (Brush)Resources["PanelAltBrush"],
                BorderBrush = (Brush)Resources["LineBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Tag = photo,
                Content = frame
            };
            thumbnail.Click += PhotoThumbnail_Click;

            ToolTipService.SetToolTip(thumbnail, photo.FileName);
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
            StatusBox.Text = "Microphone access is blocked. Enable microphone permission for ProjectCal in Windows Settings.";
        }
        catch (Exception ex)
        {
            StatusBox.Text = ex.Message;
            SyncStateText.Text = "Issue";
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
        SyncStateText.Text = _api.IsSignedIn ? "Ready" : "Offline";
    }
}
