namespace Monocle.App.ViewModels;

/// <summary>One model's free-text verdict on the selected frame (#2/#3/#4), shown as a card in the
/// detail pane's AI-critique section: <see cref="Author"/> is the model name, <see cref="Body"/> is
/// what works / what doesn't.</summary>
public sealed record CritiqueLine(string Author, string Body);
