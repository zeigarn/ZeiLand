using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZeiLand.Services;

namespace ZeiLand;

// ZEI, the voice assistant (a separate app): what it hears and answers, its questions, and the timers it
// sets on the island.
public partial class IslandWindow
{
    private const string InfoGlyphText = "";
    private const string ErrorGlyphText = "";
    private const string QuestionGlyphText = "";
    private const string ThinkingGlyphText = ""; // a light bulb

    private AssistantLink? _assistant;
    private bool _assistantListening; // or thinking, as opposed to showing an answer, which "idle" doesn't cut short
    private string? _heard; // what ZEI last heard said to it, shown under "thinking" and under an error
    private DateTime _heardAt;

    internal void AttachAssistant(AssistantLink assistant)
    {
        _assistant = assistant;
        assistant.Received += OnAssistantMessage;
        assistant.ConnectionChanged += connected =>
        {
            if (!connected && _transient == View.Assistant) EndTransient();
        };
        AssistantYesButton.Click += (_, _) =>
        {
            _assistant?.Send("confirm");
            EndTransient();
        };
        AssistantCancelButton.Click += (_, _) => DismissAssistant();
    }

    private void OnAssistantMessage(AssistantMessage message)
    {
        switch (message.Type)
        {
            case "assistant" when message.State == "listening":
                // ZEI gives up after a few seconds of silence and says so; 10 s in case it can't.
                ShowAssistant(MicrophoneGlyphText, Brushes.White, Loc.T("AssistantListening"), null, false, 10, listening: true);
                break;
            case "heard" when !string.IsNullOrWhiteSpace(message.Text):
                // What it heard, as Siri shows it: a misheard word is seen at once. The answer replaces it.
                (_heard, _heardAt) = (message.Text.Trim(), DateTime.Now);
                ShowAssistant(MicrophoneGlyphText, Brushes.White, Quoted(_heard), null, false, 5);
                break;
            case "assistant" when message.State == "thinking":
                // Its language model, for what it didn't understand at once; the answer follows.
                ShowAssistant(ThinkingGlyphText, OrangeBrush, Loc.T("AssistantThinking"), RecentlyHeard(), false, 12, listening: true);
                break;
            case "assistant":
                if (_transient == View.Assistant && _assistantListening) EndTransient();
                break;
            case "reply" when !string.IsNullOrWhiteSpace(message.Text):
                var (glyph, brush) = message.Kind switch
                {
                    "error" => (ErrorGlyphText, RedBrush),
                    "info" => (InfoGlyphText, Brushes.White),
                    _ => (CheckGlyphText, GreenBrush),
                };
                // Under an error, what it heard: why it went wrong is often there ("Kılığa" for "Claude'a").
                ShowAssistant(glyph, brush, message.Text, message.Kind == "error" ? RecentlyHeard() : null, false, message.Seconds ?? 3);
                break;
            case "confirm" when !string.IsNullOrWhiteSpace(message.Text):
                ShowAssistant(QuestionGlyphText, OrangeBrush, message.Text, Loc.T("AssistantSayYes"), true, message.Seconds ?? 12);
                break;
            case "step" when !string.IsNullOrWhiteSpace(message.Text):
                // A task on the screen under way, live: what ZEI is doing, and why in its own words. The next step replaces it.
                string step = message.Number is int number ? $"{number}. {message.Text}" : message.Text;
                ShowAssistant(ThinkingGlyphText, OrangeBrush, step, message.Detail, false, 15, listening: true);
                break;
            case "ask" when !string.IsNullOrWhiteSpace(message.Text):
                // ZEI's question, listening for the answer meanwhile; its choices are buttons.
                var choices = message.Choices?.Where(choice => !string.IsNullOrWhiteSpace(choice)).Take(6).ToList() ?? [];
                ShowAssistant(QuestionGlyphText, OrangeBrush, message.Text, choices.Count > 0 ? null : Loc.T("AssistantSayOrPick"), false,
                    message.Seconds ?? 20, choices: choices);
                break;
            case "timer":
                RunAssistantTimer(message);
                break;
        }
    }

    private void ShowAssistant(string glyph, Brush glyphBrush, string text, string? detail, bool confirm, int seconds,
        bool listening = false, IReadOnlyList<string>? choices = null)
    {
        _assistantListening = listening;
        AssistantGlyph.Text = glyph;
        AssistantGlyph.Foreground = glyphBrush;
        AssistantText.Text = text;
        AssistantText.FontWeight = confirm ? FontWeights.SemiBold : FontWeights.Normal;
        AssistantDetail.Text = detail;
        AssistantDetail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        AssistantButtons.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        // A long answer ("Atatürk 1881'de Selanik'te doğdu …") on up to three lines, not cut off after a few words.
        int lines = confirm || listening ? 1 : Math.Clamp((int)Math.Ceiling(text.Length / LettersPerLine), 1, 3);
        AssistantText.TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        AssistantText.MaxHeight = lines * LineHeight;
        AssistantView.Width = confirm ? 400 : lines > 1 || choices is { Count: > 0 } ? 440 : listening && detail is null ? 220 : 340;
        AssistantView.Height = (confirm || detail is not null ? 52 : 40) + (lines - 1) * LineHeight + ShowChoices(choices);

        // A slow breath on the microphone while it listens; it ends by itself with the listening.
        AssistantGlyph.BeginAnimation(OpacityProperty, listening
            ? new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(TimeSpan.FromSeconds(seconds)),
                EasingFunction = EaseOut,
            }
            : null);

        ShowTransient(View.Assistant, seconds * 1000);
        if (_current == View.Assistant) MorphTo(AssistantView); // from one assistant state to another: a new size
    }

    /// <summary>The question's choices as buttons; the height they take (0 without any).</summary>
    private double ShowChoices(IReadOnlyList<string>? choices)
    {
        AssistantChoices.Children.Clear();
        if (choices is not { Count: > 0 })
        {
            AssistantChoices.Visibility = Visibility.Collapsed;
            return 0;
        }
        // The buttons' rows, as they wrap in the 440 px view: each about 7 px a letter and its padding.
        double row = 0;
        int rows = 1;
        foreach (string choice in choices)
        {
            string shown = choice.Length > 24 ? choice[..23].TrimEnd() + "…" : choice;
            var button = new System.Windows.Controls.Button
            {
                Content = shown,
                Style = (Style)FindResource("PillButton"),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = choice,
            };
            button.Click += (_, _) =>
            {
                _assistant?.Send("choose", choice);
                EndTransient();
            };
            AssistantChoices.Children.Add(button);
            double width = shown.Length * 7 + 34;
            if (row + width > 370 && row > 0)
            {
                rows++;
                row = 0;
            }
            row += width;
        }
        AssistantChoices.Visibility = Visibility.Visible;
        return rows * ChoiceRowHeight + 6;
    }

    private const double ChoiceRowHeight = 32;

    // Letters on a line of the widest assistant view, and a line's height (the island's 13 px text).
    private const double LettersPerLine = 52;
    private const double LineHeight = 18;

    private static string Quoted(string text) => $"“{text}”";

    /// <summary>What ZEI heard, if it was the last thing said (within 20 s); null otherwise.</summary>
    private string? RecentlyHeard() => _heard is not null && DateTime.Now - _heardAt < TimeSpan.FromSeconds(20) ? Quoted(_heard) : null;

    /// <summary>The assistant is on the island (heard, thinking, an answer or a question).</summary>
    internal bool ShowsAssistant => _transient == View.Assistant;

    /// <summary>Clicked away: ZEI stops listening, or drops the question.</summary>
    private void DismissAssistant()
    {
        _assistant?.Send("cancel");
        EndTransient();
    }

    private void RunAssistantTimer(AssistantMessage message)
    {
        switch (message.Action)
        {
            case "start" when message.Seconds is int seconds && seconds > 0:
                _timer.StartCountdown(TimeSpan.FromSeconds(seconds));
                break;
            case "stopwatch":
                _timer.StartStopwatch();
                break;
            case "stop":
                _timer.Stop();
                // "Alarmı kapat" also puts away a "time's up".
                if (_timerDone)
                {
                    _timerDone = false;
                    Refresh();
                }
                break;
        }
    }
}
