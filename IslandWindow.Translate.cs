using System.Windows;
using DynamicIsland.Services;

namespace DynamicIsland;

// Quick translation: select text anywhere, press the shortcut, the translation shows on the island with Copy and
// "Trocar o texto" (pastes it over the selection, which is still selected in the app in front).
public partial class IslandWindow
{
    Translation? _translation;
    string _translationSource = "";
    bool _translating;

    async void TranslateSelection()
    {
        if (_translating) return;
        if (!JarvisBrain.HasKey)
        {
            ShowInfo("\uF2B7", "Tradução: falta a chave da Groq", "Configurar", glyphColor: JarvisColor,
                action: () => ((App)Application.Current).ShowSettings("jarvis"), seconds: 6);
            return;
        }
        _translating = true;
        try
        {
            if (await TextPaster.CopySelectionAsync() is not { } selected)
            {
                ShowInfo("\uF2B7", "Selecione um texto para traduzir", "", glyphColor: JarvisColor, seconds: 2.5);
                return;
            }
            ShowInfo("\uF2B7", "Traduzindo…", Preview(selected), glyphColor: JarvisColor, seconds: 30);
            _translationSource = selected;
            if (await Translator.TranslateAsync(selected, _settings.TranslateTo) is not { } translation)
            {
                ShowInfo("\uF2B7", "Não consegui traduzir agora", "tente de novo", glyphColor: Meter.Red, seconds: 3);
                return;
            }
            ShowTranslation(translation);
        }
        finally { _translating = false; }
    }

    void ShowTranslation(Translation translation)
    {
        _translation = translation;
        TranslateLangs.Text = $"{translation.From} → {translation.To}";
        TranslateText.Text = translation.Text;
        if (_expanded) SetExpanded(false);
        // long enough to read it: about 15 characters a second
        ShowTransient(Transient.Translation, Math.Clamp(6 + translation.Text.Length / 15.0, 8, 40));
    }

    /// <summary>The panel grows with the text, up to five lines.</summary>
    Size TranslationSize()
    {
        const double width = 460, sides = 36;
        TranslateText.Measure(new Size(width - sides, double.PositiveInfinity));
        // margins 12+12, header 18, gaps 6+8, buttons 30
        return new Size(width, 86 + Math.Min(TranslateText.DesiredSize.Height, TranslateText.MaxHeight));
    }

    void TranslateCopy_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_translation == null) return;
        try { Clipboard.SetText(_translation.Text); } catch { }
        TransientEnded();
    }

    async void TranslateReplace_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_translation is not { } translation) return;
        TransientEnded();
        await TextPaster.PasteAsync(translation.Text);
    }

    /// <summary>The language chip: the same text again into another language (remembered for next time).</summary>
    void TranslateLangs_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var target in Translator.Targets)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = target == "auto" ? "Automático (português ↔ inglês)" : target, IsCheckable = true, IsChecked = _settings.TranslateTo == target,
            };
            item.Click += async (_, _) =>
            {
                _settings.TranslateTo = target;
                _settings.Save();
                if (_translationSource.Length == 0) return;
                _transientTimer.Stop();
                TranslateLangs.Text = "Traduzindo…";
                if (await Translator.TranslateAsync(_translationSource, target) is { } again) ShowTranslation(again);
                else ShowInfo("\uF2B7", "Não consegui traduzir agora", "tente de novo", glyphColor: Meter.Red, seconds: 3);
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => { _menuOpen = true; _transientTimer.Stop(); };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (_transient == Transient.Translation) _transientTimer.Start();
        };
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }
}
