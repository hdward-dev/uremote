using System.Diagnostics;
using AsterDock.Contracts;
using AsterDock.UI.Services;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace AsterDock.UI.Controls;

/// <summary>
/// Attaches the shared dialog and toast layer to a window the host did not create.
/// The layer is built before the window is shown and before the page is measured.
/// A control that already belongs to a layout manager is left where it is.
/// </summary>
public static class FeedbackWindow
{
    public static void Attach(Window window, IUserFeedbackService? feedback)
    {
        try
        {
            if (feedback is not UserFeedbackService service || window.Content is not Control content) return;
            if (ContainsLayer(content)) return;
            if (window.IsVisible || HasLayoutManager(content))
            {
                Log(new InvalidOperationException("窗口已经显示或页面已经布局，不再改挂内容"));
                return;
            }

            if (ReferenceEquals(window.Content, content)) window.Content = null;
            window.Content = CreateHost(content, service);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(exception);
        }
    }

    public static bool HasLayoutManager(Control control)
        => control.IsMeasureValid
           || control.IsArrangeValid
           || control.IsAttachedToVisualTree()
           || control.GetVisualParent() is not null;

    public static Control CreateHost(Control content, UserFeedbackService service)
    {
        var root = new Grid();
        root.Children.Add(content);
        var layer = new AdFeedbackLayer { Service = service };
        layer.SetValue(Panel.ZIndexProperty, 300);
        root.Children.Add(layer);
        return root;
    }

    /// <summary>
    /// Removes a live page from its current parent so it can move into another window.
    /// </summary>
    public static bool Detach(Control page) => TryRemove(page) && IsFree(page);

    /// <summary>
    /// Puts an already detached page into a window that is not shown yet, with the feedback layer beside it.
    /// </summary>
    public static bool Adopt(Window window, Control page, UserFeedbackService service)
    {
        try
        {
            if (window.IsVisible || !IsFree(page))
            {
                Log(new InvalidOperationException("只能把已经卸下的页面放进尚未显示的窗口"));
                return false;
            }

            window.Content = CreateHost(page, service);
            return ReferenceEquals(page.Parent, window.Content);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(exception);
            return false;
        }
    }

    /// <summary>
    /// Removes a page from the host created by <see cref="Adopt"/> or <see cref="CreateHost"/>.
    /// </summary>
    public static bool Release(Control page) => TryRemove(page) && IsFree(page);

    private static bool IsFree(Control page)
        => page.Parent is null && page.GetVisualParent() is null && !page.IsAttachedToVisualTree();

    private static bool TryRemove(Control page)
    {
        try
        {
            switch (page.Parent)
            {
                case null:
                    return true;
                case Panel panel when panel.Children.Contains(page):
                    panel.Children.Remove(page);
                    return true;
                case Decorator decorator when ReferenceEquals(decorator.Child, page):
                    decorator.Child = null;
                    return true;
                case ContentControl content when ReferenceEquals(content.Content, page):
                    content.Content = null;
                    return true;
            }

            if (page.GetLogicalAncestors().OfType<ContentControl>().FirstOrDefault(content => ReferenceEquals(content.Content, page)) is { } owner)
            {
                owner.Content = null;
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(exception);
            return false;
        }
    }

    private static bool ContainsLayer(Control? control) => control switch
    {
        null => false,
        AdFeedbackLayer => true,
        Panel panel => panel.Children.OfType<Control>().Any(ContainsLayer),
        ContentControl { Content: Control inner } => ContainsLayer(inner),
        Decorator { Child: Control child } => ContainsLayer(child),
        _ => false
    };

    private static void Log(Exception exception)
        => Trace.TraceError("星栈反馈层未能挂接: " + exception);
}
