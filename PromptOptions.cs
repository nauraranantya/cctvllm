using System.Text.Json;

public static class PromptOptions {
    // The base prompt stays on disk unchanged; deployment-specific rules are appended.
    public const string Template = """
        
        Deployment settings:
        Flag activity criteria: {flag_activity}
        These criteria replace the base suspicious criteria when custom criteria are supplied.
        Evaluate only supported visual observations; context is not evidence that an event occurred.
        """;
    public static string Build(string basePrompt, ModelSettings settings) {
        var criteria = string.IsNullOrWhiteSpace(settings.FlagActivity)
            ? "Use the base prompt's suspicious criteria."
            : JsonSerializer.Serialize(settings.FlagActivity.Trim());
        var result = basePrompt + "\n" + Template.Replace("{flag_activity}", criteria);
        if (settings.SiteContextEnabled && !string.IsNullOrWhiteSpace(settings.SiteContext))
            result += "\nSite/camera context (background information only): " + JsonSerializer.Serialize(settings.SiteContext.Trim());
        if (!settings.WeaponEnabled)
            result += "\nWeapon screening is disabled. Do not assess the weapon flag. Return weapon as \"no\" for schema compatibility; the application will record it as disabled.";
        return result;
    }
}
