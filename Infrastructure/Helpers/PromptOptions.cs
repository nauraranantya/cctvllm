using System.Text.Json;

public static class PromptOptions {
    // The base prompt stays on disk unchanged; deployment-specific rules are appended.
    public const string Template = """
        
        Pengaturan penerapan:
        Kriteria aktivitas yang ditandai: {flag_activity}
        Jika kriteria khusus diberikan, kriteria ini menggantikan kriteria suspicious pada prompt dasar.
        Nilai hanya pengamatan visual yang didukung; konteks bukan bukti bahwa suatu kejadian benar-benar terjadi.
        """;
    public static string Build(string basePrompt, ModelSettings settings) {
        var criteria = string.IsNullOrWhiteSpace(settings.FlagActivity)
            ? "Gunakan kriteria suspicious pada prompt dasar."
            : JsonSerializer.Serialize(settings.FlagActivity.Trim());
        var result = basePrompt + "\n" + Template.Replace("{flag_activity}", criteria);
        if (settings.SiteContextEnabled && !string.IsNullOrWhiteSpace(settings.SiteContext))
            result += "\nKonteks lokasi/kamera (hanya informasi latar belakang): " + JsonSerializer.Serialize(settings.SiteContext.Trim());
        if (!settings.WeaponEnabled)
            result += "\nPemeriksaan senjata dinonaktifkan. Jangan menilai tanda weapon. Kembalikan weapon sebagai \"no\" agar sesuai dengan skema; aplikasi akan mencatat bahwa pemeriksaan ini dinonaktifkan.";
        return result;
    }
}
