using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WorkFileExplorer.App.Controls;

public sealed class SingleLineTabPanel : TabPanel
{
    protected override Size MeasureOverride(Size constraint)
    {
        var maxHeight = 0.0;
        var totalWidth = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            if (child is null)
            {
                continue;
            }

            child.Measure(new Size(double.PositiveInfinity, constraint.Height));
            totalWidth += child.DesiredSize.Width;
            if (child.DesiredSize.Height > maxHeight)
            {
                maxHeight = child.DesiredSize.Height;
            }
        }

        var width = double.IsInfinity(constraint.Width) ? totalWidth : constraint.Width;
        var height = double.IsInfinity(constraint.Height) ? maxHeight : Math.Min(maxHeight, constraint.Height);
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var selectedIndex = -1;
        var selectedStartX = 0.0;
        var selectedWidth = 0.0;
        var totalWidth = 0.0;
        var runningX = 0.0;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] is not FrameworkElement child)
            {
                continue;
            }

            if (selectedIndex < 0 && child is TabItem tabItem && tabItem.IsSelected)
            {
                selectedIndex = i;
                selectedStartX = runningX;
                selectedWidth = child.DesiredSize.Width;
            }

            runningX += child.DesiredSize.Width;
        }

        totalWidth = runningX;
        var viewportWidth = Math.Max(0, arrangeSize.Width);
        var maxOffset = Math.Max(0, totalWidth - viewportWidth);

        // Keep selected tab visible while distributing remaining space naturally.
        // We target a near-center alignment, then clamp to valid scroll range.
        var desiredOffset = 0.0;
        if (selectedIndex >= 0)
        {
            desiredOffset = selectedStartX - ((viewportWidth - selectedWidth) / 2.0);
        }

        var offset = Math.Clamp(desiredOffset, 0, maxOffset);
        var x = -offset;

        foreach (UIElement child in InternalChildren)
        {
            if (child is null)
            {
                continue;
            }

            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, arrangeSize.Height));
            x += width;
        }

        return arrangeSize;
    }
}
