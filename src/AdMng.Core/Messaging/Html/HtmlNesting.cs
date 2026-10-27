using AngleSharp.Dom;

namespace AdMng.Core.Messaging.Html;

/// <summary>The nesting guard of every tree walk over message HTML (<see cref="MessageHtmlLimits.MaxNestingDepth"/>).</summary>
internal static class HtmlNesting
{
    /// <summary>True when an element lies more than <see cref="MessageHtmlLimits.MaxNestingDepth"/> levels below
    /// <paramref name="root"/>. Iterative (an explicit stack), so it is safe on any parsed tree.</summary>
    public static bool IsTooDeep(INode root)
    {
        var stack = new Stack<(INode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > MessageHtmlLimits.MaxNestingDepth)
            {
                return true;
            }

            foreach (var child in node.ChildNodes)
            {
                if (child is IElement)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        return false;
    }
}
