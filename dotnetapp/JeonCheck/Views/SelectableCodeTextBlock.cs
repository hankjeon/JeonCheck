using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using JeonCheck.Services;

namespace JeonCheck.Views;

// 기존 줄 가상화와 토큰 툴팁을 유지하면서 한 줄 안에서만 선택한다.
public class SelectableCodeTextBlock : TextBlock
{
    public static readonly DependencyProperty SearchRangesProperty = DependencyProperty.Register(
        nameof(SearchRanges), typeof(IReadOnlyList<CodeTextRange>), typeof(SelectableCodeTextBlock),
        new PropertyMetadata(null, (d, _) => ((SelectableCodeTextBlock)d).RenderSpans()));

    public IReadOnlyList<CodeTextRange>? SearchRanges
    {
        get => (IReadOnlyList<CodeTextRange>?)GetValue(SearchRangesProperty);
        set => SetValue(SearchRangesProperty, value);
    }

    private IReadOnlyList<CodeSegment> _spans = Array.Empty<CodeSegment>();
    private string _code = string.Empty;
    private CodeTextRange _selection;
    private CodeTextRange _wordAnchor;
    private int _anchor;
    private bool _wordDrag;
    private static readonly Brush SelectionBrush = FrozenBrush("#264F78");
    private static readonly Brush SearchBrush = FrozenBrush("#755715");

    public event EventHandler? SelectionStarted;
    public event EventHandler<string>? SearchRequested;
    public string SelectedText => _code.Substring(_selection.Start, _selection.Length);

    public SelectableCodeTextBlock()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;
        Unloaded += (_, _) => ClearSelection();
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    internal void SetCodeSpans(IReadOnlyList<CodeSegment>? spans)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        _spans = spans ?? Array.Empty<CodeSegment>();
        _code = string.Concat(_spans.Select(s => s.Text));
        _selection = default;
        RenderSpans();
    }

    public void ClearSelection() => SelectRange(default);

    private void SelectRange(CodeTextRange range)
    {
        if (_selection == range) return;
        _selection = range;
        RenderSpans();
    }

    private void RenderSpans()
    {
        Inlines.Clear();
        int offset = 0;
        foreach (var span in _spans)
        {
            int end = offset + span.Text.Length;
            var boundaries = new SortedSet<int> { offset, end };
            void AddRange(CodeTextRange range)
            {
                if (range.Start > offset && range.Start < end) boundaries.Add(range.Start);
                int rangeEnd = range.Start + range.Length;
                if (rangeEnd > offset && rangeEnd < end) boundaries.Add(rangeEnd);
            }
            AddRange(_selection);
            if (SearchRanges != null)
                foreach (var range in SearchRanges) AddRange(range);
            int start = offset;
            foreach (int stop in boundaries.Skip(1))
            {
                var run = CodeHighlightBehavior.CreateRun(new CodeSegment(
                    span.Text.Substring(start - offset, stop - start), span.ForegroundHex, span.IsItalic, span.HoverTip));
                if (_selection.Length > 0 && start >= _selection.Start && start < _selection.Start + _selection.Length)
                    run.Background = SelectionBrush;
                else if (SearchRanges?.Any(r => start >= r.Start && start < r.Start + r.Length) == true)
                    run.Background = SearchBrush;
                Inlines.Add(run);
                start = stop;
            }
            offset = end;
        }
    }

    private TextPointer? PositionAt(Point point)
    {
        // 선택 배경으로 Run을 재구성한 직후의 연속 클릭·드래그에서도 최신 문자 위치를 사용한다.
        UpdateLayout();
        return GetPositionFromPoint(new Point(Math.Clamp(point.X, 0, ActualWidth), ActualHeight / 2), true);
    }

    private int PointerIndex(TextPointer? position) => position == null ? 0 :
        Math.Clamp(new TextRange(ContentStart, position).Text.Length, 0, _code.Length);

    private int IndexAt(Point point) => PointerIndex(PositionAt(point));

    private int WordIndexAt(Point point)
    {
        var position = PositionAt(point);
        int index = PointerIndex(position);
        // 삽입 위치가 아닌 실제 포인터 아래 문자를 기준으로 단어/괄호/공백을 구분한다.
        if (index > 0 && position != null && point.X < position.GetCharacterRect(LogicalDirection.Forward).X) index--;
        return index;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        SelectionStarted?.Invoke(this, EventArgs.Empty);
        Focus();
        DependencyObject? parent = this;
        while (parent != null && parent is not ListBoxItem) parent = VisualTreeHelper.GetParent(parent);
        if (parent is ListBoxItem item) item.IsSelected = true;
        _anchor = IndexAt(e.GetPosition(this));
        _wordDrag = e.ClickCount >= 2;
        if (_wordDrag)
        {
            _wordAnchor = CodeTextSelection.WordAt(_code, WordIndexAt(e.GetPosition(this)));
            SelectRange(_wordAnchor);
        }
        else SelectRange(new(_anchor, 0));
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        int index = IndexAt(e.GetPosition(this));
        SelectRange(_wordDrag
            ? CodeTextSelection.ExtendWord(_code, _wordAnchor, WordIndexAt(e.GetPosition(this)))
            : new(Math.Min(_anchor, index), Math.Abs(index - _anchor)));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // 우클릭은 ListBox의 줄 선택이나 기존 문자 선택을 바꾸지 않는다.
        e.Handled = true;
        int index = IndexAt(e.GetPosition(this));
        if (_selection.Length == 0 || index < _selection.Start || index > _selection.Start + _selection.Length) return;
        string selected = SelectedText;
        var item = new MenuItem { Header = "선택한 텍스트 검색" };
        item.Click += (_, _) => SearchRequested?.Invoke(this, selected);
        var menu = new ContextMenu { PlacementTarget = this };
        menu.Items.Add(item);
        menu.IsOpen = true;
    }

    public void BringSearchIntoView()
    {
        if (SearchRanges == null || SearchRanges.Count == 0) return;
        int index = SearchRanges[0].Start;
        foreach (Run run in Inlines)
        {
            if (index <= run.Text.Length)
            {
                var pointer = run.ContentStart.GetPositionAtOffset(index);
                if (pointer != null) BringIntoView(pointer.GetCharacterRect(LogicalDirection.Forward));
                return;
            }
            index -= run.Text.Length;
        }
    }
}
