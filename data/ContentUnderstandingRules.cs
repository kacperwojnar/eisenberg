namespace Eisenberg.Data;

public static class ContentUnderstandingRules
{
    // The import value wins, then the user value, then the global value. A null override does not count.
    public static bool IsEnabled(bool global, bool? user, bool? import) => import ?? user ?? global;
}
