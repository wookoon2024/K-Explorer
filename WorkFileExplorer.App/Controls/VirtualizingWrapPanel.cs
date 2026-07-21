using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WorkFileExplorer.App.Controls;

/// <summary>
/// A uniform-cell wrap panel that virtualizes its children: only the containers
/// for items currently in (or just outside) the viewport are realized, and they
/// are recycled as the user scrolls. This keeps tile/compact list views fast and
/// memory-flat even for folders with tens of thousands of items — a plain
/// <see cref="WrapPanel"/> realizes every item up front, which is what made
/// large/image-heavy folders stutter.
///
/// Both supported view modes lay items out as uniform cells (tiles are square;
/// the compact list uses a fixed row height), so the layout math stays simple:
/// columns = floor(viewportWidth / ItemWidth). <see cref="ItemWidth"/> and
/// <see cref="ItemHeight"/> must both be set for virtualization to engage.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

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

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public ScrollViewer? ScrollOwner { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    private static bool IsValid(double value) => !double.IsNaN(value) && value > 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemWidth = ItemWidth;
        var itemHeight = ItemHeight;
        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;

        // Accessing InternalChildren hooks up the item container generator; this
        // must happen before any generator call or GenerateNext throws.
        _ = InternalChildren;
        var generator = (IItemContainerGenerator)ItemContainerGenerator;

        if (itemCount == 0 || !IsValid(itemWidth) || !IsValid(itemHeight))
        {
            // Nothing to virtualize (or cell size not yet known): drop all containers.
            CleanupContainers(0, -1, itemCount);
            UpdateScrollMetrics(new Size(0, 0), availableSize);
            return new Size(0, 0);
        }

        var viewportWidth = double.IsInfinity(availableSize.Width) ? itemWidth : availableSize.Width;
        var viewportHeight = double.IsInfinity(availableSize.Height) ? itemHeight : availableSize.Height;

        var columns = Math.Max(1, (int)(viewportWidth / itemWidth));
        _columns = columns;
        var rows = (itemCount + columns - 1) / columns;
        var extent = new Size(columns * itemWidth, rows * itemHeight);

        UpdateScrollMetrics(extent, new Size(viewportWidth, viewportHeight));

        var maxOffsetY = Math.Max(0, extent.Height - viewportHeight);
        if (_offset.Y > maxOffsetY)
        {
            _offset.Y = maxOffsetY;
            ScrollOwner?.InvalidateScrollInfo();
        }

        // Visible rows plus a one-row buffer above and below for smooth scrolling.
        var firstRow = Math.Max(0, (int)(_offset.Y / itemHeight) - 1);
        var lastRow = Math.Min(rows - 1, (int)((_offset.Y + viewportHeight) / itemHeight) + 1);
        var firstIndex = firstRow * columns;
        var lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);

        RealizeRange(generator, firstIndex, lastIndex, new Size(itemWidth, itemHeight));
        CleanupContainers(firstIndex, lastIndex, itemCount);

        return new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = ItemWidth;
        var itemHeight = ItemHeight;
        var generator = ItemContainerGenerator;

        if (!IsValid(itemWidth) || !IsValid(itemHeight))
        {
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }

            return finalSize;
        }

        var columns = Math.Max(1, _columns);
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0)
            {
                continue;
            }

            var row = itemIndex / columns;
            var col = itemIndex % columns;
            children[i].Arrange(new Rect(
                col * itemWidth - _offset.X,
                row * itemHeight - _offset.Y,
                itemWidth,
                itemHeight));
        }

        return finalSize;
    }

    private void RealizeRange(IItemContainerGenerator generator, int firstIndex, int lastIndex, Size childSize)
    {
        if (lastIndex < firstIndex)
        {
            return;
        }

        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var i = firstIndex; i <= lastIndex; i++, childIndex++)
            {
                var child = (UIElement?)generator.GenerateNext(out var newlyRealized);
                if (child is null)
                {
                    break;
                }

                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                child.Measure(childSize);
            }
        }
    }

    private void CleanupContainers(int firstIndex, int lastIndex, int itemCount)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex < 0)
            {
                continue;
            }

            if (itemIndex < firstIndex || itemIndex > lastIndex || itemIndex >= itemCount)
            {
                ((IItemContainerGenerator)generator).Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void BringIndexIntoView(int index)
    {
        // ScrollIntoView / keyboard navigation asks for an item that may not be
        // realized yet. Scroll so its row is in view, then force a synchronous
        // layout so the container exists when the caller queries for it.
        if (index < 0 || !IsValid(ItemHeight))
        {
            return;
        }

        var columns = Math.Max(1, _columns);
        var row = index / columns;
        var top = row * ItemHeight;
        var bottom = top + ItemHeight;

        if (top < _offset.Y)
        {
            SetVerticalOffset(top);
        }
        else if (bottom > _offset.Y + _viewport.Height)
        {
            SetVerticalOffset(bottom - _viewport.Height);
        }

        UpdateLayout();
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
        }

        // A reset (folder change) invalidates every realized container; force a
        // fresh pass and snap back to the top.
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _offset.Y = 0;
        }

        InvalidateMeasure();
    }

    private void UpdateScrollMetrics(Size extent, Size viewport)
    {
        var changed = false;
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

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private double WheelAmount => Math.Max(48, IsValid(ItemHeight) ? Math.Min(ItemHeight, 96) : 48)
        * Math.Max(1, SystemParameters.WheelScrollLines);

    private double LineAmount => IsValid(ItemHeight) ? Math.Min(ItemHeight, 48) : 16;

    public void LineUp() => SetVerticalOffset(_offset.Y - LineAmount);

    public void LineDown() => SetVerticalOffset(_offset.Y + LineAmount);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - WheelAmount);

    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + WheelAmount);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    // Horizontal scrolling is disabled for this panel (columns always fit the width).
    public void LineLeft() { }

    public void LineRight() { }

    public void MouseWheelLeft() { }

    public void MouseWheelRight() { }

    public void PageLeft() { }

    public void PageRight() { }

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var maxOffsetY = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Max(0, Math.Min(offset, maxOffsetY));
        if (Math.Abs(offset - _offset.Y) < 0.5)
        {
            return;
        }

        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        // Called by ScrollIntoView / keyboard navigation. Find which item the
        // target visual belongs to and scroll just enough to reveal its row.
        var child = FindDirectChild(visual);
        if (child is null || !IsValid(ItemHeight))
        {
            return rectangle;
        }

        var childIndex = InternalChildren.IndexOf(child);
        if (childIndex < 0)
        {
            return rectangle;
        }

        var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0)
        {
            return rectangle;
        }

        var columns = Math.Max(1, _columns);
        var row = itemIndex / columns;
        var top = row * ItemHeight;
        var bottom = top + ItemHeight;

        if (top < _offset.Y)
        {
            SetVerticalOffset(top);
        }
        else if (bottom > _offset.Y + _viewport.Height)
        {
            SetVerticalOffset(bottom - _viewport.Height);
        }

        return rectangle;
    }

    private UIElement? FindDirectChild(Visual visual)
    {
        DependencyObject? current = visual;
        while (current is not null)
        {
            if (current is UIElement element && InternalChildren.Contains(element))
            {
                return element;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
