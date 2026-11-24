// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CouchSubs.Core;
using CouchSubs.Core.Dubbing;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>Ayarlardaki Türkçe dublaj bölümü (Mac: DubbingCard.swift). Ses motoru pakette yoksa görünmez.</summary>
public partial class DubbingCard : UserControl
{
    private AppController? controller;
    private bool loading;

    public DubbingCard()
    {
        InitializeComponent();
        // Kesme işaretli metinler XAML biçimlendirme uzantısına yazılamıyor.
        SetupHint.Text = Localizer.Current["In the game's audio settings, turn the dialogue (voice) volume down; keep music and effects. If the game can show speaker names in subtitles, turn them on: every character then speaks with its own voice."];
        Credits.Text = Localizer.Current["Voices: Pocket TTS Turkish (wite-tech, CC BY 4.0) with Kyutai's pocket-tts. The voices are synthetic, not real people."];
        CharacterVoicesOn.Content = Localizer.Current["Use the characters' own voices"];
        Credits.Text += "\n" + Localizer.Current["Character voice recognition: WeSpeaker ResNet34 (VoxCeleb, CC BY 4.0)."];
        Unloaded += (_, _) =>
        {
            if (controller is not null) controller.PropertyChanged -= OnChanged;
        };
    }

    internal void Attach(AppController attached)
    {
        controller = attached;
        Visibility = attached.DubbingAvailable ? Visibility.Visible : Visibility.Collapsed;
        attached.PropertyChanged += OnChanged;
        Refresh();
    }

    private static string Label(DubbingVoice voice) => Localizer.Current[voice switch
    {
        DubbingVoice.Female1 => "Woman 1 (bright)",
        DubbingVoice.Female2 => "Woman 2 (warm)",
        DubbingVoice.Female3 => "Woman 3 (confident)",
        DubbingVoice.Male1 => "Man 1 (calm, deep)",
        DubbingVoice.Male2 => "Man 2 (energetic)",
        _ => "Man 3 (crisp)",
    }];

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppController.Settings) or nameof(AppController.DubbingInstalled)
            or nameof(AppController.DubbingDownloading) or nameof(AppController.DubbingProgress) or nameof(AppController.DubbingError)
            or nameof(AppController.CharacterVoicesReady) or nameof(AppController.GameSpeechInstalled))
            Refresh();
    }

    private void Refresh()
    {
        if (controller is null) return;
        loading = true;
        var settings = controller.Settings.Dubbing;
        Enabled.IsChecked = settings.Enabled;
        EnginePanel.Visibility = controller.FastDubbingAvailable ? Visibility.Visible : Visibility.Collapsed;
        var fast = controller.FastDubbingAvailable && settings.Engine == DubbingEngine.Fast;
        (fast ? FastEngine : NaturalEngine).IsChecked = true;
        EngineHint.Text = Localizer.Current[fast
            ? "Windows' own Turkish voice: speaks at once, nothing to download. Sounds more mechanical."
            : "Natural voices, downloaded once. Each line takes a moment to prepare, so the voice can come a little after the subtitle."];
        Credits.Visibility = fast ? Visibility.Collapsed : Visibility.Visible;
        Options.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = controller.DubbingInstalled ? Visibility.Collapsed : Visibility.Visible;
        ReadyPanel.Visibility = controller.DubbingInstalled ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.IsEnabled = !controller.DubbingDownloading;
        Progress.Visibility = ProgressText.Visibility = controller.DubbingDownloading ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = controller.DubbingProgress;
        ProgressText.Text = Localizer.Current["Downloading voices… %lld%%"]
            .Replace("%lld", ((int)(controller.DubbingProgress * 100)).ToString()).Replace("%%", "%");
        (settings.Output == DubbingOutput.Tv ? OnTv : OnComputer).IsChecked = true;
        Error.Text = controller.DubbingError ?? "";
        Error.Visibility = controller.DubbingError is null ? Visibility.Collapsed : Visibility.Visible;
        BuildVoices(settings.Cast);
        RefreshCharacterVoices(settings, fast);
        loading = false;
    }

    private void RefreshCharacterVoices(DubbingSettings settings, bool fast)
    {
        if (controller is null) return;
        CharacterVoicesOn.IsChecked = settings.CharacterVoices;
        CharacterVoicesOn.IsEnabled = !fast;
        CharacterHint.Text = Localizer.Current[!controller.GameSpeechInstalled
            ? "First download the speech model under Settings › Game (translate speech without subtitles); it is also used to collect the voices."
            : fast
                ? "Only works with the Natural voice."
                : "While you play, the characters' voices are collected on this computer (nothing is uploaded). Each character is then dubbed with a voice similar to its own. Works best when you replay a game."];
        var ready = controller.CharacterVoicesReady;
        CharacterCount.Text = Localizer.Current["%lld characters' voices are ready"].Replace("%lld", ready.ToString());
        ClearVoices.IsEnabled = ready > 0 || controller.IsRunning;
    }

    private void OnCharacterVoices(object sender, RoutedEventArgs e) =>
        Update(s => s with { Dubbing = s.Dubbing with { CharacterVoices = CharacterVoicesOn.IsChecked == true } });

    private void OnClearVoices(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        var answer = MessageBox.Show(Window.GetWindow(this), Localizer.Current["Delete all collected character voices for this game?"],
            Localizer.Current["Delete collected voices"], MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) controller.ClearCharacterVoices();
    }

    private void BuildVoices(VoiceCast cast)
    {
        Voices.Children.Clear();
        Voices.Children.Add(VoiceRow(Localizer.Current["Narrator and unnamed lines"], cast.Narrator,
            voice => Update(s => s with { Dubbing = s.Dubbing with { Cast = WithRoles(s.Dubbing.Cast, narrator: voice) } })));
        Voices.Children.Add(VoiceRow(Localizer.Current["Second speaker in dialogue"], cast.Partner,
            voice => Update(s => s with { Dubbing = s.Dubbing with { Cast = WithRoles(s.Dubbing.Cast, partner: voice) } })));
        if (cast.Assignments.Count == 0) return;
        Voices.Children.Add(new TextBlock { Text = Localizer.Current["Characters"], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        foreach (var name in cast.Assignments.Keys.Order(StringComparer.Ordinal))
            Voices.Children.Add(VoiceRow(DisplayName(name), cast.Assignments[name], voice => controller?.AssignVoice(voice, name)));
    }

    private static VoiceCast WithRoles(VoiceCast cast, DubbingVoice? narrator = null, DubbingVoice? partner = null)
    {
        var copy = cast.Copy();
        if (narrator is { } n) copy.Narrator = n;
        if (partner is { } p) copy.Partner = p;
        return copy;
    }

    /// <summary>"OLD MAN" → "Old Man"</summary>
    private static string DisplayName(string name) =>
        string.Join(' ', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private void OnEngine(object sender, RoutedEventArgs e)
    {
        if (loading || sender is not RadioButton { Tag: string tag }) return;
        var engine = Enum.Parse<DubbingEngine>(tag);
        Update(s => s with { Dubbing = s.Dubbing with { Engine = engine } });
    }

    private Grid VoiceRow(string title, DubbingVoice selected, Action<DubbingVoice> choose)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        var picker = new ComboBox { Margin = new Thickness(0, 0, 8, 0) };
        foreach (var voice in DubbingVoices.All)
            picker.Items.Add(new ComboBoxItem { Content = Label(voice), Tag = voice, IsSelected = voice == selected });
        picker.SelectionChanged += (_, _) =>
        {
            if (!loading && picker.SelectedItem is ComboBoxItem { Tag: DubbingVoice voice }) choose(voice);
        };
        var listen = new Button { Content = Localizer.Current["Listen"] };
        listen.Click += async (_, _) =>
        {
            if (controller is not null && picker.SelectedItem is ComboBoxItem { Tag: DubbingVoice voice }) await controller.PreviewVoiceAsync(voice);
        };
        Grid.SetColumn(picker, 1);
        Grid.SetColumn(listen, 2);
        row.Children.Add(label);
        row.Children.Add(picker);
        row.Children.Add(listen);
        return row;
    }

    private void Update(Func<UserSettings, UserSettings> change)
    {
        if (!loading && controller is not null) controller.Settings = change(controller.Settings);
    }

    private void OnEnabled(object sender, RoutedEventArgs e) =>
        Update(s => s with { Dubbing = s.Dubbing with { Enabled = Enabled.IsChecked == true } });

    private void OnOutput(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) Update(s => s with { Dubbing = s.Dubbing with { Output = Enum.Parse<DubbingOutput>(tag) } });
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (controller is not null) await controller.DownloadDubbingAsync();
    }
}
