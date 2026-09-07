using System.Windows;
using System.Windows.Media;

namespace VRCAvatarChanger;

/// <summary>ビジュアルツリーの探索。「クリックされた要素がどの行か」「一覧の中のスクロールビューア」などに使う。</summary>
internal static class VisualTree
{
    /// <summary>origin 自身から親へたどって、最初に見つかった T。無ければ null。</summary>
    public static T? FindAncestor<T>(DependencyObject? origin) where T : DependencyObject
    {
        while (origin is not null && origin is not T) origin = VisualTreeHelper.GetParent(origin);
        return origin as T;
    }

    /// <summary>root の子孫を深さ優先でたどって、最初に見つかった T。無ければ null。</summary>
    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>root の子孫のうち T のものすべて (深さ優先)。</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
