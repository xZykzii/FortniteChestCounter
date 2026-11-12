using System.Globalization;

namespace ChestCounter;

/// <summary>
/// Idioma de la aplicación. Por defecto el de Windows entre los soportados (ver <see cref="Supported"/>; si no es
/// ninguno, inglés); se puede elegir otro en Ajustes. Los textos se escriben en línea: <c>L.T("español", "English")</c>; las traducciones
/// a los demás idiomas están en <see cref="Translations"/>, indexadas por el texto español.
/// Los nombres guardados en data.json (tipos de cofre, objetos del tesoro) siguen en francés por compatibilidad y se traducen al mostrarlos (<see cref="Name"/>).
/// </summary>
public static class L
{
    /// <summary>
    /// Códigos de idioma soportados. Los dos primeros son el español (texto de origen) y el inglés; el resto
    /// están en el mismo orden que las filas de <see cref="Translations"/>.
    /// </summary>
    private static readonly string[] Supported = ["es", "en", "pt", "ru", "fr", "de", "it", "pl", "tr", "ja"];

    private static readonly Dictionary<string, string> Cultures = new()
    {
        ["es"] = "es-ES",
        ["en"] = "en-US",
        ["pt"] = "pt-BR",
        ["ru"] = "ru-RU",
        ["fr"] = "fr-FR",
        ["de"] = "de-DE",
        ["it"] = "it-IT",
        ["pl"] = "pl-PL",
        ["tr"] = "tr-TR",
        ["ja"] = "ja-JP",
    };

    /// <summary>Idiomas que se pueden elegir en Ajustes, con su nombre en ese mismo idioma.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("es", "Español"), ("en", "English"), ("pt", "Português (Brasil)"), ("ru", "Русский"), ("fr", "Français"),
        ("de", "Deutsch"), ("it", "Italiano"), ("pl", "Polski"), ("tr", "Türkçe"), ("ja", "日本語"),
    ];

    /// <summary>
    /// Código del idioma actual ("es", "en", "pt", "ru", "fr", "de", "it", "pl", "tr" o "ja").
    /// Por defecto el de Windows; <see cref="Use"/> aplica el elegido en Ajustes. Para probar: la variable
    /// CHESTCOUNTER_LANG fuerza el idioma por encima de todo.
    /// </summary>
    public static string Code { get; private set; } = Detect();

    /// <summary>True cuando la interfaz va en inglés.</summary>
    public static bool En => Code == "en";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Cultures[Code]);

    /// <summary>
    /// Posición del idioma actual en las filas de <see cref="Translations"/>: 0 = pt … 7 = ja.
    /// Vale -2 para español y -1 para inglés (esos textos ya están en el código).
    /// </summary>
    private static int Slot = Array.IndexOf(Supported, Code) - 2;

    /// <summary>Idioma de Windows (o el forzado con CHESTCOUNTER_LANG), sin tener en cuenta lo elegido en Ajustes.</summary>
    public static string SystemCode => Detect();

    /// <summary>
    /// Aplica el idioma elegido en Ajustes (null o desconocido = el de Windows). Se llama al arrancar, antes de crear
    /// las ventanas: los textos se leen al construirlas, así que un cambio necesita reiniciar el contador.
    /// </summary>
    public static void Use(string? code)
    {
        var chosen = Environment.GetEnvironmentVariable("CHESTCOUNTER_LANG") is { Length: > 0 } || code == null || Array.IndexOf(Supported, code) < 0
            ? Detect()
            : code;
        Code = chosen;
        Culture = CultureInfo.GetCultureInfo(Cultures[chosen]);
        Slot = Array.IndexOf(Supported, chosen) - 2;
    }

    private static string Detect()
    {
        var code = Environment.GetEnvironmentVariable("CHESTCOUNTER_LANG") is { Length: > 0 } forced
            ? forced
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        code = code.Length >= 2 ? code[..2].ToLowerInvariant() : "";
        return Array.IndexOf(Supported, code) >= 0 ? code : "en";
    }

    /// <summary>Texto en el idioma actual: <paramref name="es"/> en español, <paramref name="en"/> en inglés y la traducción en los demás.</summary>
    public static string T(string es, string en) => Pick(es, es, en);
    public static string T(string fr, string en, string es) => Code == "fr" ? fr : T(es, en);

    /// <summary>Texto largo, identificado por <paramref name="id"/> en <see cref="Translations"/> (clave "#id").</summary>
    public static string Big(string id, string es, string en) => Pick("#" + id, es, en);

    /// <summary>Texto con huecos {0}, {1}… (formato de <c>string.Format</c>), traducido como <see cref="T"/>.</summary>
    public static string F(string es, string en, params object?[] args) => string.Format(Culture, T(es, en), args);

    private static string Pick(string key, string es, string en)
    {
        if (Slot == -2)
        {
            return es;
        }
        if (Slot == -1)
        {
            return en;
        }
        return Translations.Table.TryGetValue(key, out var row) && row[Slot] is { Length: > 0 } text ? text : en;
    }

    public static string N(int n) => n.ToString("0", Culture);

    /// <summary>"3 cofres" / "1 chest" (plural según la cifra, en el idioma actual).</summary>
    public static string Count(int n, string esOne, string esMany, string enOne, string enMany) =>
        $"{N(n)} {(n == 1 ? T(esOne, enOne) : T(esMany, enMany))}";

    /// <summary>"12.º" / "12th" / "12.".</summary>
    public static string Ordinal(int n) => Code switch
    {
        "es" => $"{n}.º",
        "pt" or "it" => $"{n}º",
        "fr" => n == 1 ? "1er" : $"{n}e",
        "ja" => $"{n}位",
        "en" => n + EnglishSuffix(n),
        _ => $"{n}.",
    };

    private static string EnglishSuffix(int n) =>
        (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    /// <summary>Fecha corta + hora de una partida ("05/11 22:54" / "Nov 5 22:54").</summary>
    public static string DateTimeShort(DateTime d) => d.ToString(En ? "MMM d HH:mm" : "dd/MM HH:mm", Culture);

    // Nombres internos guardados en data.json (en inglés) -> nombre mostrado.
    private static readonly Dictionary<string, (string Es, string En)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chest"] = ("Cofre", "Chest"),
        ["Rare chest"] = ("Cofre raro", "Rare chest"),
        ["Buried chest"] = ("Cofre enterrado", "Buried chest"),
        ["Extraction / relic chest"] = ("Cofre de extracción / reliquias", "Extraction / relic chest"),
        ["Spirit poster chest"] = ("Cofre de los carteles de espíritu", "Spirit poster chest"),
        ["Other chest"] = ("Otro cofre", "Other chest"),
        ["Loot cannon"] = ("Cañón de botín", "Loot cannon"),
        ["Ammo box"] = ("Caja de munición", "Ammo box"),
        ["Food box"] = ("Caja de comida", "Food box"),
        ["Other container"] = ("Otro contenedor", "Other container"),
        ["Treasure"] = ("Tesoro", "Treasure"),
        ["Treasure (unknown type)"] = ("Tesoro (tipo desconocido)", "Treasure (unknown type)"),
        ["Treasure gizmo"] = ("Artilugio de tesoro", "Treasure gizmo"),
        ["Golden firework"] = ("Fuego artificial dorado", "Golden firework"),
        ["Portable extractor"] = ("Extractor portátil", "Portable extractor"),
        ["Spirit locator"] = ("Localizador de espíritu", "Spirit locator"),
        ["Lucky locator"] = ("Localizador afortunado", "Lucky locator"),
        ["Extraction accelerator"] = ("Acelerador de extracción", "Extraction accelerator"),
        ["unknown"] = ("desconocido", "unknown"),
    };

    /// <summary>
    /// Nombres internos de versiones anteriores a la 2.0.9 (estaban en francés) -> nombre interno actual.
    /// Solo se usa para convertir el historial antiguo al abrirlo.
    /// </summary>
    private static readonly Dictionary<string, string> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coffre"] = "Chest",
        ["Coffre rare"] = "Rare chest",
        ["Coffre enterré"] = "Buried chest",
        ["Coffre d'extraction / reliques"] = "Extraction / relic chest",
        ["Coffre des affiches d'esprit"] = "Spirit poster chest",
        ["Autre coffre"] = "Other chest",
        ["Canon à butin"] = "Loot cannon",
        ["Caisse de munitions"] = "Ammo box",
        ["Caisse de nourriture"] = "Food box",
        ["Autre conteneur"] = "Other container",
        ["Trésor"] = "Treasure",
        ["Trésor (type inconnu)"] = "Treasure (unknown type)",
        ["Gizmo de trésor"] = "Treasure gizmo",
        ["Feu d'artifice doré"] = "Golden firework",
        ["Extracteur portable"] = "Portable extractor",
        ["Localisateur d'esprit"] = "Spirit locator",
        ["Localisateur chanceux"] = "Lucky locator",
        ["Accélérateur d'extraction"] = "Extraction accelerator",
        ["inconnu"] = "unknown",
    };

    /// <summary>Nombre interno actual de un nombre guardado por una versión anterior (o el mismo si no cambió).</summary>
    public static string Upgrade(string stored) => Legacy.TryGetValue(stored, out var current) ? current : stored;

    /// <summary>Nombre mostrado de un tipo de cofre u objeto guardado en data.json.</summary>
    public static string Name(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return stored ?? "";
        }
        return Names.TryGetValue(Upgrade(stored), out var pair) ? T(pair.Es, pair.En) : stored;
    }
}
