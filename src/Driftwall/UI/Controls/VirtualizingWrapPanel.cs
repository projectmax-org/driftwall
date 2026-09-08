using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Driftwall.UI.Controls;

/// <summary>
/// A wrap panel that only realises the tiles currently on screen.
/// <para>
/// WPF ships no virtualising wrap panel, and the browse grid can hold thousands of photos. Without
/// virtualisation every tile would decode a bitmap and stay in memory, which is precisely the
/// behaviour this app is meant to avoid. Uniform item size is assumed, which the photo grid
/// guarantees, and that makes the visible range a matter of arithmetic rather than measurement.
/// </para>
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(240d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(160d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Rows of tiles kept realised above and below the viewport, to smooth fast scrolling.</summary>
    private const int OverscanRows = 1;

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

    /// <summary>
    /// The concrete generator. <see cref="VirtualizingPanel.ItemContainerGenerator"/> is typed as the
    /// interface, which omits <c>IndexFromContainer</c>; going through the owning ItemsControl gets
    /// the real type without a cast.
    /// </summary>
    private ItemContainerGenerator? Generator => ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Touching InternalChildren is what forces the generator to attach; without it the panel
        // reports an empty item count on the very first pass.
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        if (itemsOwner is null) return default;

        _ = InternalChildren;
        int itemCount = itemsOwner.Items.Count;

        double itemWidth = Math.Max(1, ItemWidth);
        double itemHeight = Math.Max(1, ItemHeight);

        double width = double.IsInfinity(availableSize.Width) ? itemWidth : availableSize.Width;
        _columns = Math.Max(1, (int)(width / itemWidth));
        int rows = itemCount == 0 ? 0 : (int)Math.Ceiling((double)itemCount / _columns);

        var extent = new Size(_columns * itemWidth, rows * itemHeight);
        var viewport = new Size(width, double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);

        UpdateScrollInfo(extent, viewport);

        var (first, last) = VisibleRange(itemCount, itemHeight);
        RealizeRange(first, last, new Size(itemWidth, itemHeight));

        return new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = Generator;
        if (generator is null) return finalSize;

        double itemWidth = Math.Max(1, ItemWidth);
        double itemHeight = Math.Max(1, ItemHeight);

        foreach (UIElement child in InternalChildren)
        {
            int index = generator.IndexFromContainer(child);
            if (index < 0) continue;

            int row = index / _columns;
            int column = index % _columns;

            child.Arrange(new Rect(
                column * itemWidth - _offset.X,
                row * itemHeight - _offset.Y,
                itemWidth,
                itemHeight));
        }

        return finalSize;
    }

    private (int First, int Last) VisibleRange(int itemCount, double itemHeight)
    {
        if (itemCount == 0) return (0, -1);

        int firstRow = Math.Max(0, (int)(_offset.Y / itemHeight) - OverscanRows);
        int visibleRows = (int)Math.Ceiling(_viewport.Height / itemHeight) + 1 + OverscanRows * 2;

        int first = firstRow * _columns;
        int last = Math.Min(itemCount - 1, first + visibleRows * _columns - 1);

        return (Math.Min(first, itemCount - 1), last);
    }

    private void RealizeRange(int first, int last, Size itemSize)
    {
        var generator = ItemContainerGenerator;
        if (last < first)
        {
            CleanUpOutside(0, -1);
            return;
        }

        var startPosition = generator.GeneratorPositionFromIndex(first);

        // An offset of 0 means the position lands on a realised container, in which case generation
        // must start at the next one to avoid duplicating it.
        int childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (int i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement?)generator.GenerateNext(out bool isNewlyRealized);
                if (child is null) break;

                if (isNewlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.Measure(itemSize);
            }
        }

        CleanUpOutside(first, last);
    }

    /// <summary>Recycles containers that have scrolled out of range, releasing their bitmaps.</summary>
    private void CleanUpOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;

        for (int i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);

            if (itemIndex >= first && itemIndex <= last) continue;
            if (itemIndex < 0) continue;

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // The generator has already dropped every container; the panel must follow suit or
                // it keeps stale children alive and leaks their decoded images.
                RemoveInternalChildRange(0, InternalChildren.Count);
                SetVerticalOffset(0);
                break;
        }

        InvalidateMeasure();
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        bool changed = false;

        if (extent != _extent)
        {
            _extent = extent;
            changed = true;
        }

        if (viewport != _viewport)
        {
            _viewport = viewport;
            changed = true;
        }

        // Content can shrink under the current offset, for example when a filter is applied.
        double maxOffset = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > maxOffset)
        {
            _offset.Y = maxOffset;
            changed = true;
        }

        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    // ---------------- IScrollInfo ----------------

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private double LineHeight => Math.Max(16, ItemHeight / 3);

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineHeight);
    public void LineDown() => SetVerticalOffset(VerticalOffset + LineHeight);
    public void PageUp() => SetVerticalOffset(VerticalOffset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(VerticalOffset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - LineHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + LineHeight * 3);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        double clamped = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(clamped - _offset.Y) < 0.5) return;

        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual as UIElement;
        if (child is null) return rectangle;

        int index = Generator?.IndexFromContainer(child) ?? -1;
        if (index < 0) return rectangle;

        double top = index / _columns * ItemHeight;
        double bottom = top + ItemHeight;

        if (top < VerticalOffset) SetVerticalOffset(top);
        else if (bottom > VerticalOffset + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);

        return rectangle;
    }
}
