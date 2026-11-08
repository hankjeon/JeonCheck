using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using JeonCheck.Services;

namespace JeonCheck.Views;

// 코드 줄 토큰 색상(HighlightSpans)을 TextBlock Inlines(Run)로 표시하는 첨부 동작.
// 색이 없는 구간은 Run 색상을 지정하지 않아 줄 상태 색상(검사 완료·결함·미검사 등)을 상속한다.
public static class CodeHighlightBehavior
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans",
        typeof(IReadOnlyList<CodeSegment>),
        typeof(CodeHighlightBehavior),
        new PropertyMetadata(null, OnSpansChanged));

    public static void SetSpans(DependencyObject d, IReadOnlyList<CodeSegment>? value) => d.SetValue(SpansProperty, value);

    public static IReadOnlyList<CodeSegment>? GetSpans(DependencyObject d) => (IReadOnlyList<CodeSegment>?)d.GetValue(SpansProperty);

    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();

    private static void OnSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        textBlock.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<CodeSegment> spans)
            return;

        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.ForegroundHex != null)
            {
                if (!BrushCache.TryGetValue(span.ForegroundHex, out var brush))
                {
                    brush = (SolidColorBrush)new BrushConverter().ConvertFromString(span.ForegroundHex)!;
                    brush.Freeze();
                    BrushCache[span.ForegroundHex] = brush;
                }
                run.Foreground = brush;
            }
            if (span.IsItalic)
                run.FontStyle = FontStyles.Italic;
            // HoverTip이 null이면 팝업을 표시하지 않는다.
            if (!string.IsNullOrEmpty(span.HoverTip))
                ToolTipService.SetToolTip(run, span.HoverTip);
            textBlock.Inlines.Add(run);
        }
    }
}
