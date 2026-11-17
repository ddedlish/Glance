using System.Text.RegularExpressions;

namespace Glance;

/// <summary>
/// Model knowledge base. Recognises the accessory from its Bluetooth name and decides
/// the window layout (earbuds + case, over-ear, neckband, speaker…) and where
/// per-earbud data can come from. Extend <see cref="Brands"/> to add new models.
/// </summary>
internal static class Profiles
{
    const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    sealed record Rule(string Pattern, DeviceKind Kind, bool Case = false)
    {
        public readonly Regex Rx = new(Pattern, O);
    }

    sealed record Brand(string Name, string Detect, Rule[] Rules, Color? Color = null, string? StripPrefix = null)
    {
        public readonly Regex Rx = new(Detect, O);
    }

    static readonly Color JblOrange = Color.FromArgb(255, 102, 0);

    // Order matters: brands are checked top to bottom, and inside a brand the first matching rule wins.
    // Kinds: Earbuds (+case), Headphones, Headset, Neckband, Speaker, Mouse, Keyboard, Gamepad, Pen, Watch.
    static readonly Brand[] Brands =
    {
        // ───────────── Apple ecosystem ─────────────
        new("Apple", @"\bAirPods\b|Magic\s?(Mouse|Keyboard|Trackpad)|Apple\s?Pencil|Siri\s?Remote", new Rule[]
        {
            new(@"AirPods\s?Max", DeviceKind.Headphones),
            new(@"AirPods", DeviceKind.Earbuds, true),
            new(@"Magic\s?Keyboard", DeviceKind.Keyboard),
            new(@"Magic\s?(Mouse|Trackpad)", DeviceKind.Mouse),
            new(@"Pencil", DeviceKind.Pen),
        }),
        new("Beats", @"\bBeats|Powerbeats", new Rule[]
        {
            new(@"Beats\s?(Solo|Studio)(?!\s?Buds)\s?(Pro|\d)?|Beats\s?EP", DeviceKind.Headphones),
            new(@"Beats\s?Flex|BeatsX|Powerbeats\s?3|Powerbeats(?!\s?Pro)", DeviceKind.Neckband),
            new(@"Pill", DeviceKind.Speaker),
            new(@".", DeviceKind.Earbuds, true),
        }),

        // ───────────── Harman ─────────────
        new("JBL", @"\bJBL", new Rule[]
        {
            new(@"\b(Flip|Charge|Clip|Go|Xtreme|Boombox|PartyBox|Pulse|Link|Encore|Authentics|Horizon|Grip|Wind|Bar)\b", DeviceKind.Speaker),
            new(@"Quantum\s?(\d{3}|ONE|Stream|Wireless)|Quantum\s?910", DeviceKind.Headset),
            new(@"TWS|Buds|Beam|Flex|Live\s?Pro|Live\s?Free|Tour\s?Pro|Endurance\s?(Peak|Race|Dive)|Reflect\s?(Flow|Aero|Mini\s?NC)|Soundgear|Sense|Quantum\s?TWS|Club\s?Pro\+|Wave\s?\d{3}|Vibe\s?\d{3}|Tune\s?(Buds|Beam|Flex)", DeviceKind.Earbuds, true),
            new(@"Live\s?[12]\d{2}BT|Tune\s?[12]\d{2}BT|Endurance\s?Run|Reflect\s?Fit|\bT[12]\d{2}BT|Duet\s?Mini", DeviceKind.Neckband),
            new(@".", DeviceKind.Headphones),
        }, JblOrange, "JBL"),
        new("Harman Kardon", @"Harman\s?Kardon|\bHK\s|Onyx\s?Studio|\bFLY\s?(TWS|ANC|BT)", new Rule[]
        {
            new(@"FLY\s?TWS", DeviceKind.Earbuds, true),
            new(@"FLY", DeviceKind.Headphones),
            new(@".", DeviceKind.Speaker),
        }, StripPrefix: "Harman Kardon"),
        new("AKG", @"\bAKG\b", new Rule[]
        {
            new(@"N400|N5005|Y\d{3}\s?TWS", DeviceKind.Earbuds, true),
            new(@"N200", DeviceKind.Neckband),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "AKG"),

        // ───────────── Samsung / Google / Android ─────────────
        new("Samsung", @"Galaxy\s?(Buds|Watch|Fit|Ring)|\bLevel\s?(U|On|Box)|Samsung", new Rule[]
        {
            new(@"Buds", DeviceKind.Earbuds, true),
            new(@"Watch|Fit|Ring", DeviceKind.Watch),
            new(@"Level\s?U", DeviceKind.Neckband),
            new(@"Level\s?Box|Speaker", DeviceKind.Speaker),
            new(@"Keyboard", DeviceKind.Keyboard),
            new(@"Mouse", DeviceKind.Mouse),
            new(@"Level\s?On", DeviceKind.Headphones),
        }, StripPrefix: "Samsung"),
        new("Google", @"Pixel\s?(Buds|Watch)|Stadia\s?Controller", new Rule[]
        {
            new(@"Buds", DeviceKind.Earbuds, true),
            new(@"Watch", DeviceKind.Watch),
            new(@"Controller", DeviceKind.Gamepad),
        }),
        new("Nothing", @"Nothing\s?(Ear|Headphone)|\bCMF\s?(Buds|Headphone|Neckband|Watch)|^Ear\s?\(\w+\)|^Ear\s?\(?(open|a|1|2|3)\)?$", new Rule[]
        {
            new(@"Headphone", DeviceKind.Headphones),
            new(@"Neckband", DeviceKind.Neckband),
            new(@"Watch", DeviceKind.Watch),
            new(@".", DeviceKind.Earbuds, true),
        }),
        new("Huawei", @"FreeBuds|FreeClip|FreeLace|FreeArc|HUAWEI|Watch\s?(GT|Fit|D)\b", new Rule[]
        {
            new(@"FreeLace", DeviceKind.Neckband),
            new(@"FreeBuds\s?Studio", DeviceKind.Headphones),
            new(@"FreeBuds|FreeClip|FreeArc", DeviceKind.Earbuds, true),
            new(@"Sound\s?(Joy|X)|Speaker", DeviceKind.Speaker),
            new(@"Pencil|M-?Pen", DeviceKind.Pen),
            new(@"Keyboard", DeviceKind.Keyboard),
            new(@"Mouse", DeviceKind.Mouse),
            new(@"Watch|Band", DeviceKind.Watch),
        }, StripPrefix: "HUAWEI"),
        new("Honor", @"HONOR\s?(Earbuds|Choice|Magic|Watch|Band)", new Rule[]
        {
            new(@"Watch|Band", DeviceKind.Watch),
            new(@"Pen", DeviceKind.Pen),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "HONOR"),
        new("Xiaomi", @"Redmi\s?(Buds|Watch|Band)|Xiaomi|Mi\s?(True\s?Wireless|Earbuds|Air\s?\d|Neckband|Band|Smart\s?Band|Portable|Bluetooth\s?Speaker|Dual\s?Mode|Silent|Wireless\s?Mouse)|POCO\s?Buds", new Rule[]
        {
            new(@"Neckband", DeviceKind.Neckband),
            new(@"Band|Watch", DeviceKind.Watch),
            new(@"Speaker|Sound", DeviceKind.Speaker),
            new(@"Mouse", DeviceKind.Mouse),
            new(@"Keyboard", DeviceKind.Keyboard),
            new(@"Pen", DeviceKind.Pen),
            new(@"Controller|Gamepad", DeviceKind.Gamepad),
            new(@"Headphone", DeviceKind.Headphones),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Xiaomi"),
        new("OnePlus", @"OnePlus\s?(Nord\s?)?Buds|OnePlus\s?Bullets|OnePlus\s?Watch", new Rule[]
        {
            new(@"Bullets", DeviceKind.Neckband),
            new(@"Watch", DeviceKind.Watch),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "OnePlus"),
        new("OPPO", @"OPPO|\bEnco\b", new Rule[]
        {
            new(@"Enco\s?M", DeviceKind.Neckband),
            new(@"Watch|Band", DeviceKind.Watch),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "OPPO"),
        new("realme", @"realme", new Rule[]
        {
            new(@"Buds\s?Wireless|Neckband", DeviceKind.Neckband),
            new(@"Watch|Band", DeviceKind.Watch),
            new(@"Speaker", DeviceKind.Speaker),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "realme"),
        new("vivo", @"\bvivo\s?(TWS|Wireless|Watch)", new Rule[]
        {
            new(@"Wireless\s?Sport|Neckband", DeviceKind.Neckband),
            new(@"Watch", DeviceKind.Watch),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "vivo"),
        new("Motorola", @"moto\s?(buds|g\s?Watch|watch)|Motorola|VerveBuds", new Rule[]
        {
            new(@"Watch", DeviceKind.Watch),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Motorola"),

        // ───────────── Sony ─────────────
        new("Sony", @"(?<![A-Za-z0-9])W[FHI]-|LinkBuds|\bSRS-|ULT\s?(Field|Wear|Tower)|\bMDR-|INZONE|DualSense|DualShock|^Wireless Controller$|\bSony\b", new Rule[]
        {
            new(@"DualSense|DualShock|Controller", DeviceKind.Gamepad),
            new(@"INZONE\s?Buds|(?<![A-Za-z0-9])WF-|LinkBuds", DeviceKind.Earbuds, true),
            new(@"INZONE", DeviceKind.Headset),
            new(@"(?<![A-Za-z0-9])WI-", DeviceKind.Neckband),
            new(@"\bSRS-|ULT\s?(Field|Tower)|Speaker", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }),

        // ───────────── Audio specialists ─────────────
        new("Soundcore", @"Soundcore|\bAnker\b", new Rule[]
        {
            new(@"Space\s?(Q\d+|One)|Life\s?(Q\d+|Tune)|\bQ\d{2}\b", DeviceKind.Headphones),
            new(@"Motion|Boom|Flare|Select|Rave|Icon", DeviceKind.Speaker),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Soundcore"),
        new("Marshall", @"Marshall|\b(MOTIF|MINOR|MODE|MAJOR|MONITOR|EMBERTON|WILLEN|STANMORE|ACTON|WOBURN|MIDDLETON|TUFTON|KILBURN)\s(II|III|IV|V|A\.N\.C\.)", new Rule[]
        {
            new(@"Motif|Minor|Mode", DeviceKind.Earbuds, true),
            new(@"Emberton|Willen|Stanmore|Acton|Woburn|Middleton|Tufton|Kilburn", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Marshall"),
        new("Bose", @"\bBose\b|QuietComfort|SoundLink|\bQC\s?(Ultra|Earbuds|\d{2})|SoundSport", new Rule[]
        {
            new(@"Earbuds|Ultra\s?Open|Sport\s?Earbuds|SoundSport\s?Free", DeviceKind.Earbuds, true),
            new(@"SoundSport", DeviceKind.Neckband),
            new(@"SoundLink|Home\s?Speaker|Portable|Smart\s?Speaker", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Bose"),
        new("Sennheiser", @"Sennheiser|MOMENTUM|ACCENTUM|\bCX\s(Plus|True|\d)|\bPXC\b|\bHD\s?\d{3}BT|IE\s?\d{3}\s?Pro", new Rule[]
        {
            new(@"True\s?Wireless|\bTW\b|Sport|\bCX\b|ACCENTUM\s?True", DeviceKind.Earbuds, true),
            new(@"\bCX\s?\d{3}BT|IE\s?\d{3}", DeviceKind.Neckband),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Sennheiser"),
        new("Jabra", @"Jabra", new Rule[]
        {
            new(@"Elite\s?\d{1,2}(\s?(Active|t|Pro))?(?!\s?(45h|85h))|Evolve2\s?Buds|Connect\s?5t", DeviceKind.Earbuds, true),
            new(@"Evolve|Engage|Talk|Speak", DeviceKind.Headset),
            new(@"Speak", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Jabra"),
        new("Poly", @"\bPoly\b|Plantronics|Voyager|BackBeat|Blackwire|Savi", new Rule[]
        {
            new(@"Voyager\s?Free|BackBeat\s?(Pro\s?5100|Fit\s?3\d{3})", DeviceKind.Earbuds, true),
            new(@"Voyager\s?(Legend|5200|\d{4}\s?Office)", DeviceKind.Headset),
            new(@"BackBeat\s?(Go|Fit\s?\d{3}$)", DeviceKind.Neckband),
            new(@".", DeviceKind.Headset),
        }, StripPrefix: "Plantronics"),
        new("Audio-Technica", @"Audio-?Technica|\bATH-", new Rule[]
        {
            new(@"ATH-(CKS|CKR|SQ|TWX|ANC300TW|CK3TW)", DeviceKind.Earbuds, true),
            new(@"ATH-(CK\d{3}XBT|CKR\d{3}BT)", DeviceKind.Neckband),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Audio-Technica"),
        new("Shure", @"\bShure\b|AONIC", new Rule[]
        {
            new(@"AONIC\s?(Free|215)", DeviceKind.Earbuds, true),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Shure"),
        new("Bang & Olufsen", @"Beoplay|Beosound|Beolit|B&O|Bang\s?&?\s?Olufsen", new Rule[]
        {
            new(@"Beoplay\s?(E\d|EQ|EX)", DeviceKind.Earbuds, true),
            new(@"Beosound|Beolit|A1|A9|Explore", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Bang & Olufsen"),
        new("Bowers & Wilkins", @"Bowers|\bB&W\b|\bP[xi]\s?\d|\bPX\d", new Rule[]
        {
            new(@"\bPi\s?\d", DeviceKind.Earbuds, true),
            new(@"Zeppelin", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Bowers & Wilkins"),
        new("Beyerdynamic", @"beyerdynamic|Amiron|Aventho|Lagoon|Free\s?BYRD|Xelento", new Rule[]
        {
            new(@"Free\s?BYRD|Xelento\s?Wireless", DeviceKind.Earbuds, true),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "beyerdynamic"),
        new("Technics", @"Technics|\bEAH-AZ|\bEAH-A\d", new Rule[]
        {
            new(@"EAH-AZ", DeviceKind.Earbuds, true),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Technics"),
        new("Panasonic", @"Panasonic|\bRZ-[SB]\d|\bRB-M\d", new Rule[]
        {
            new(@"RZ-[SB]", DeviceKind.Earbuds, true),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Panasonic"),
        new("Philips", @"Philips|\bTA[TAHN]\d{4}|\bSHB\d{4}", new Rule[]
        {
            new(@"\bTAT\d{4}", DeviceKind.Earbuds, true),
            new(@"\bTAN\d{4}|\bTAA\d{4}", DeviceKind.Neckband),
            new(@"\bTAS\d{4}|Speaker", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "Philips"),
        new("Skullcandy", @"Skullcandy|\b(Indy|Sesh|Dime|Grind|Jib|Rail|Smokin)\s?(Evo|Fuel|ANC|True|XT|Buds|\d)|Crusher|Hesh\s?(ANC|Evo|\d)|Riff\s?Wireless|Push\s?(Active|Ultra|Play)", new Rule[]
        {
            new(@"Crusher|Hesh|Riff|Cassette|Venue", DeviceKind.Headphones),
            new(@"Method|Ink'?d|Jib\s?Wireless", DeviceKind.Neckband),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Skullcandy"),
        new("Urbanears", @"Urbanears|\b(Jakan|Plattan\s?\d|Pampas|Alby|Luma)\b", new Rule[]
        {
            new(@"Pampas|Plattan", DeviceKind.Headphones),
            new(@"Rålis|Ralis|Stammen|Lotsen", DeviceKind.Speaker),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Urbanears"),
        new("House of Marley", @"House\s?of\s?Marley|\bMarley\b|Positive\s?Vibration|Get\s?Together|No\s?Bounds", new Rule[]
        {
            new(@"Positive\s?Vibration|Exodus", DeviceKind.Headphones),
            new(@"Get\s?Together|No\s?Bounds|Bag\s?of\s?Riddim", DeviceKind.Speaker),
            new(@".", DeviceKind.Earbuds, true),
        }),
        new("Ultimate Ears", @"\bUE\s|WONDERBOOM|MEGABOOM|EPICBOOM|HYPERBOOM|\bBOOM\s?\d|EVERBOOM", new Rule[] { new(@".", DeviceKind.Speaker) }, StripPrefix: "UE"),
        new("Tribit", @"Tribit|StormBox|XSound|MoveBuds|FlyBuds", new Rule[]
        {
            new(@"StormBox|XSound|Speaker", DeviceKind.Speaker),
            new(@"QuietPlus", DeviceKind.Headphones),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Tribit"),
        new("Edifier", @"EDIFIER|NeoBuds|\bW\d{3}(NB|BT|TWS)|\bX\d\s?Lite|\bGM\d\b", new Rule[]
        {
            new(@"NeoBuds|TWS|\bX\d|\bW2\d{2}T|\bGM\d", DeviceKind.Earbuds, true),
            new(@"\bW\d{3}BT\s?Plus|\bW\d{3}NB", DeviceKind.Headphones),
            new(@"\bMR\d|\bR\d{4}|\bMP\d{3}|Speaker", DeviceKind.Speaker),
            new(@".", DeviceKind.Headphones),
        }, StripPrefix: "EDIFIER"),
        new("1MORE", @"1MORE|ColorBuds|ComfoBuds|SonoFlow|EVO\s?TWS|PistonBuds|AERO", new Rule[]
        {
            new(@"SonoFlow", DeviceKind.Headphones),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "1MORE"),
        new("QCY", @"\bQCY\b|MeloBuds|ArcBuds|AilyBuds|\bHT0\d|\bQCY-", new Rule[]
        {
            new(@"H\d\b|\bH[23]\s?Pro", DeviceKind.Headphones),
            new(@"Watch", DeviceKind.Watch),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "QCY"),
        new("Haylou", @"Haylou|MoriPods", new Rule[]
        {
            new(@"S35|S30", DeviceKind.Headphones),
            new(@"Watch|LS\d{2}|Solar", DeviceKind.Watch),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Haylou"),
        new("SoundPEATS", @"SoundPEATS|Capsule3|Opera\s?0\d", new Rule[]
        {
            new(@"Space", DeviceKind.Headphones),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "SoundPEATS"),
        new("EarFun", @"EarFun|UBOOM", new Rule[]
        {
            new(@"Wave|Tune", DeviceKind.Headphones),
            new(@"UBOOM", DeviceKind.Speaker),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "EarFun"),
        new("TOZO", @"\bTOZO\b", new Rule[]
        {
            new(@"HT2", DeviceKind.Headphones),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "TOZO"),
        new("Baseus", @"Baseus|Bowie|\bEncok\b", new Rule[]
        {
            new(@"Bowie\s?(D|H)\d", DeviceKind.Headphones),
            new(@"Mouse", DeviceKind.Mouse),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Baseus"),
        new("UGREEN", @"UGREEN|HiTune", new Rule[]
        {
            new(@"Max\d", DeviceKind.Headphones),
            new(@"Mouse", DeviceKind.Mouse),
            new(@"Keyboard", DeviceKind.Keyboard),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "UGREEN"),
        new("Lenovo", @"Lenovo|ThinkPad|ThinkPlus|\bLP\d{1,2}\b|\bXT\d{2}\b|Legion|Yoga\s?(Mouse|Pen)", new Rule[]
        {
            new(@"Keyboard|TrackPoint", DeviceKind.Keyboard),
            new(@"Mouse", DeviceKind.Mouse),
            new(@"Pen", DeviceKind.Pen),
            new(@"Controller|Legion\s?(Go|Controller)", DeviceKind.Gamepad),
            new(@"Headset|Legion\s?H", DeviceKind.Headset),
            new(@"Headphone|TH\d{2}", DeviceKind.Headphones),
            new(@".", DeviceKind.Earbuds, true),
        }, StripPrefix: "Lenovo"),
        new("Monster", @"\bMonster\b|Clarity|Airmars", new Rule[] { new(@".", DeviceKind.Earbuds, true) }, StripPrefix: "Monster"),
        new("Nura", @"\bnura", new Rule[] { new(@"True", DeviceKind.Earbuds, true), new(@".", DeviceKind.Headphones) }),
        new("Denon", @"Denon|PerL", new Rule[] { new(@"PerL", DeviceKind.Earbuds, true), new(@".", DeviceKind.Headphones) }, StripPrefix: "Denon"),
        new("Pioneer", @"Pioneer|\bSE-[CS]\d", new Rule[] { new(@"SE-C", DeviceKind.Earbuds, true), new(@".", DeviceKind.Headphones) }, StripPrefix: "Pioneer"),
        new("Audeze", @"Audeze|Maxwell|Penrose|Mobius", new Rule[] { new(@".", DeviceKind.Headset) }, StripPrefix: "Audeze"),

        // ───────────── Russian ecosystem ─────────────
        new("Яндекс", @"Yandex|Яндекс|\bStation\s?(Mini|Max|Lite|Midi|Duo|2)|Станция", new Rule[]
        {
            new(@"Buds|Наушники", DeviceKind.Earbuds, true),
            new(@".", DeviceKind.Speaker),
        }, StripPrefix: "Yandex"),
        new("Sber", @"Sber|SberBoom|Сбер", new Rule[] { new(@"Buds", DeviceKind.Earbuds, true), new(@".", DeviceKind.Speaker) }, StripPrefix: "Sber"),
        new("VK", @"\bVK\s?(Капсула|Kapsula|Capsula)|Капсула", new Rule[] { new(@".", DeviceKind.Speaker) }),

        // ───────────── Gaming audio ─────────────
        new("SteelSeries", @"SteelSeries|Arctis|Aerox|Rival\s?\d|Apex\s?(Pro|\d)|Stratus|Nimbus\+?", new Rule[]
        {
            new(@"Arctis\s?GameBuds|GameBuds", DeviceKind.Earbuds, true),
            new(@"Arctis", DeviceKind.Headset),
            new(@"Apex", DeviceKind.Keyboard),
            new(@"Stratus|Nimbus", DeviceKind.Gamepad),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "SteelSeries"),
        new("HyperX", @"HyperX|Cloud\s?(Flight|Alpha|Stinger|III|Mix|MIX|Orbit)|Pulsefire|Alloy|Clutch", new Rule[]
        {
            new(@"Cloud\s?MIX\s?Buds|Cirro\s?Buds", DeviceKind.Earbuds, true),
            new(@"Cloud", DeviceKind.Headset),
            new(@"Alloy", DeviceKind.Keyboard),
            new(@"Clutch", DeviceKind.Gamepad),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "HyperX"),
        new("Corsair", @"Corsair|Virtuoso|\bHS\d{2}|Dark\s?Core|Harpoon|Sabre|Katar|\bK\d{2}\s?(Wireless|Air)|Scimitar|Ironclaw|M\d{2}\s?(RGB|Ultra)", new Rule[]
        {
            new(@"Virtuoso|HS\d{2}|Void", DeviceKind.Headset),
            new(@"\bK\d{2}|Keyboard", DeviceKind.Keyboard),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "Corsair"),
        new("Turtle Beach", @"Turtle\s?Beach|Stealth\s?(\d{3}|Pro)|Recon|Burst|Kone|Vulcan|ROCCAT", new Rule[]
        {
            new(@"Stealth|Recon\s?(\d{3})?$|Atlas", DeviceKind.Headset),
            new(@"Recon\s?Controller|Stealth\s?Ultra|Controller", DeviceKind.Gamepad),
            new(@"Vulcan|Keyboard", DeviceKind.Keyboard),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "Turtle Beach"),
        new("ASUS", @"\bASUS\b|\bROG\b|\bTUF\b|Raikiri|Cetra|Delta|Harpe|Keris|Gladius|Falchion|Azoth|Strix", new Rule[]
        {
            new(@"Cetra", DeviceKind.Earbuds, true),
            new(@"Delta|Strix\s?Go|Fusion|Pelta", DeviceKind.Headset),
            new(@"Raikiri|Controller", DeviceKind.Gamepad),
            new(@"Falchion|Azoth|Claymore|Keyboard", DeviceKind.Keyboard),
            new(@"Pen", DeviceKind.Pen),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "ASUS"),

        // ───────────── Peripherals ─────────────
        new("Logitech", @"Logi|Logitech|\bMX\s|^M\d{3}|^K\d{3}|POP\s?(Mouse|Keys)|Pebble|\bLift\b|\bG\d{3}\b|G\s?PRO|\bZone\b|\bAstro\b|\bG\s?Cloud|\bF710|Ergo\s?K860|Signature\s?(M|K)\d", new Rule[]
        {
            new(@"Keys|Keyboard|\bK\d{3}|Craft|MX\s?Mechanical|G\d{3}\s?(TKL|Keyboard)|\bG915|\bG815|\bG613|\bG715|\bG713", DeviceKind.Keyboard),
            new(@"Zone\s?(True|Wireless\s?Buds)|G\s?FITS", DeviceKind.Earbuds, true),
            new(@"Zone|Headset|Astro|G\d{3}\s?X|\bG435|\bG733|\bG935|\bG535|PRO\s?X\s?(2|Wireless)|\bA50|\bA30", DeviceKind.Headset),
            new(@"G\s?Cloud|F710|Controller", DeviceKind.Gamepad),
            new(@"Spotlight|\bR\d{3}\b|Presenter", DeviceKind.Pen),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "Logitech"),
        new("Microsoft", @"Microsoft|Surface|Xbox|Arc\s?Mouse|Designer|Modern\s?(Mobile|Wireless)", new Rule[]
        {
            new(@"Controller|Elite|Adaptive", DeviceKind.Gamepad),
            new(@"Headset|Headphones", DeviceKind.Headphones),
            new(@"Earbuds", DeviceKind.Earbuds, true),
            new(@"Keyboard|Keys", DeviceKind.Keyboard),
            new(@"Pen", DeviceKind.Pen),
            new(@"Mouse|Arc|Mice|Precision", DeviceKind.Mouse),
            new(@"Speaker", DeviceKind.Speaker),
        }, StripPrefix: "Microsoft"),
        new("Attack Shark", @"Attack\s?Shark|^AS[-\s]?(G3|X3|X11|R1|V6|X6|X8)", new Rule[]
        {
            new(@"K\d{2}|Keyboard", DeviceKind.Keyboard),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "Attack Shark"),
        new("Razer", @"Razer|Hammerhead|Barracuda|Kraken|BlackShark|Orochi|Basilisk|DeathAdder|Viper|Naga|Pro\s?Click|BlackWidow|Wolverine|Kishi|Raiju|Atrox|Huntsman|Cobra", new Rule[]
        {
            new(@"Hammerhead", DeviceKind.Earbuds, true),
            new(@"Barracuda|Kraken|BlackShark|Opus|Nari", DeviceKind.Headset),
            new(@"Wolverine|Kishi|Raiju|Controller", DeviceKind.Gamepad),
            new(@"BlackWidow|Huntsman|DeathStalker|Pro\s?Type|Keyboard|Ornata", DeviceKind.Keyboard),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "Razer"),
        new("Keychron", @"Keychron|^K\d{1,2}(\s?(Pro|Max|SE|HE))?$|^Q\d{1,2}(\s?(Pro|Max|HE))?$|^V\d(\s?Max)?$", new Rule[]
        {
            new(@"\bM\d|Mouse", DeviceKind.Mouse),
            new(@".", DeviceKind.Keyboard),
        }, StripPrefix: "Keychron"),
        new("NuPhy", @"NuPhy|Air\s?(60|75|96)|Halo\s?(65|75|96)|Gem80|Kick75", new Rule[] { new(@".", DeviceKind.Keyboard) }, StripPrefix: "NuPhy"),
        new("Lofree", @"Lofree|Flow\s?84|OE\d{3}", new Rule[] { new(@"Mouse|Touch", DeviceKind.Mouse), new(@".", DeviceKind.Keyboard) }, StripPrefix: "Lofree"),
        new("Rapoo", @"Rapoo|\bMT\d{3}|\bE\d{4}", new Rule[] { new(@"Keyboard|\bE\d{4}|\bK\d{3}|\bV\d{3}", DeviceKind.Keyboard), new(@"Headset", DeviceKind.Headset), new(@".", DeviceKind.Mouse) }, StripPrefix: "Rapoo"),
        new("A4Tech", @"A4Tech|Bloody|\bFstyler|\bFB\d{2}", new Rule[] { new(@"Keyboard|FBK", DeviceKind.Keyboard), new(@".", DeviceKind.Mouse) }, StripPrefix: "A4Tech"),
        new("HP", @"\bHP\b|OMEN|HyperX\s?Pulsefire", new Rule[]
        {
            new(@"Keyboard", DeviceKind.Keyboard),
            new(@"Pen", DeviceKind.Pen),
            new(@"Headset|Blast|Frequency", DeviceKind.Headset),
            new(@"Speaker", DeviceKind.Speaker),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "HP"),
        new("Dell", @"\bDell\b|Alienware|\bMS\d{4}|\bKM\d{4}|\bWL\d{4}|\bPN\d{4}", new Rule[]
        {
            new(@"Keyboard|\bKB\d{3}|\bKM\d{4}", DeviceKind.Keyboard),
            new(@"Headset|\bWL\d{4}|Alienware.*Headset", DeviceKind.Headset),
            new(@"Pen|\bPN\d{4}", DeviceKind.Pen),
            new(@".", DeviceKind.Mouse),
        }, StripPrefix: "Dell"),
        new("Satechi", @"Satechi", new Rule[] { new(@"Keyboard|Keys", DeviceKind.Keyboard), new(@".", DeviceKind.Mouse) }, StripPrefix: "Satechi"),
        new("Glorious", @"Glorious|Model\s?[ODI]\b|GMMK", new Rule[] { new(@"GMMK|Keyboard", DeviceKind.Keyboard), new(@".", DeviceKind.Mouse) }, StripPrefix: "Glorious"),
        new("Pulsar", @"Pulsar|Xlite|X2\s?(V2|Mini|H)", new Rule[] { new(@".", DeviceKind.Mouse) }, StripPrefix: "Pulsar"),
        new("Zowie", @"ZOWIE|\bEC\d-C|\bFK\d", new Rule[] { new(@".", DeviceKind.Mouse) }, StripPrefix: "ZOWIE"),
        new("Wacom", @"Wacom|Intuos|Bamboo|Cintiq|One\s?by\s?Wacom", new Rule[] { new(@".", DeviceKind.Pen) }, StripPrefix: "Wacom"),
        new("XP-Pen", @"XP-?PEN|Deco\s?(Pro|Mini|LW)", new Rule[] { new(@".", DeviceKind.Pen) }, StripPrefix: "XP-PEN"),
        new("Huion", @"Huion|Inspiroy|Kamvas", new Rule[] { new(@".", DeviceKind.Pen) }, StripPrefix: "Huion"),

        // ───────────── Game controllers ─────────────
        new("Nintendo", @"Pro\s?Controller|Joy-?Con|Nintendo|NSO\s?(SNES|N64|Genesis)|Switch\s?2?\s?Controller", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "Nintendo"),
        new("8BitDo", @"8BitDo|\bSN30|\bSF30|Ultimate\s?(2C|C|Bluetooth)|Lite\s?2|Zero\s?2|Retro\s?Mechanical", new Rule[]
        {
            new(@"Keyboard|Retro\s?Mechanical", DeviceKind.Keyboard),
            new(@"Mouse", DeviceKind.Mouse),
            new(@".", DeviceKind.Gamepad),
        }, StripPrefix: "8BitDo"),
        new("GameSir", @"GameSir|\bG7\s?SE|\bT4\s?(Pro|Cyclone|Kaleid|Nova)|Nova\s?Lite|Cyclone\s?2", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "GameSir"),
        new("Flydigi", @"Flydigi|Vader\s?\d|Direwolf", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "Flydigi"),
        new("PowerA", @"PowerA|MOGA|Fusion\s?Pro|Enhanced\s?Wireless", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "PowerA"),
        new("Backbone", @"Backbone", new Rule[] { new(@".", DeviceKind.Gamepad) }),
        new("Valve", @"Steam\s?Controller|Valve|Steam\s?Deck", new Rule[] { new(@".", DeviceKind.Gamepad) }),
        new("NVIDIA", @"NVIDIA|SHIELD\s?(Controller|Remote)", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "NVIDIA"),
        new("Amazon", @"Luna\s?Controller|Echo\s?(Buds|Dot|Pop|Studio)|Fire\s?TV\s?Remote", new Rule[]
        {
            new(@"Controller", DeviceKind.Gamepad),
            new(@"Buds", DeviceKind.Earbuds, true),
            new(@".", DeviceKind.Speaker),
        }),
        new("Thrustmaster", @"Thrustmaster|eSwap|T\.Flight|Hotas", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "Thrustmaster"),
        new("Hori", @"\bHORI\b|Split\s?Pad|Onyx\+|Fighting\s?Commander", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "HORI"),
        new("iPega", @"iPega|\bPG-\d{4}", new Rule[] { new(@".", DeviceKind.Gamepad) }, StripPrefix: "iPega"),

        // ───────────── Wearables ─────────────
        new("Garmin", @"Garmin|Forerunner|fenix|Fēnix|Venu|vívo|vivo(active|move|smart)|Instinct|Epix|HRM-", new Rule[] { new(@".", DeviceKind.Watch) }, StripPrefix: "Garmin"),
        new("Amazfit", @"Amazfit|Zepp|\bGTR\b|\bGTS\b|T-Rex|Bip|Balance|Helio", new Rule[] { new(@"PowerBuds|Buds", DeviceKind.Earbuds, true), new(@".", DeviceKind.Watch) }, StripPrefix: "Amazfit"),
        new("Fitbit", @"Fitbit|Charge\s?\d|Versa|Sense\s?\d|Inspire|Luxe", new Rule[] { new(@".", DeviceKind.Watch) }, StripPrefix: "Fitbit"),
        new("Polar", @"\bPolar\b|Vantage|Grit\s?X|Pacer|Ignite|Verity|\bH10\b", new Rule[] { new(@".", DeviceKind.Watch) }, StripPrefix: "Polar"),
        new("Shokz", @"Shokz|AfterShokz|OpenRun|OpenMove|OpenComm|OpenSwim|OpenFit|OpenDots|Aeropex", new Rule[]
        {
            new(@"OpenFit|OpenDots", DeviceKind.Earbuds, true),
            new(@"OpenComm", DeviceKind.Headset),
            new(@".", DeviceKind.Neckband),
        }, StripPrefix: "Shokz"),
    };

    static readonly Regex AncRx = new(@"\bA?NC\b|\d{2,3}\s?NC\b|\bPro\b|Tour|Live\s?(Free|Beam|Flex|Buds)|QuietComfort|\bQC\b|Noise\s?Cancel|\bMax\b|\bWH-1000|\bWF-1000|Ultra", O);
    static readonly Regex JblSmartCase = new(@"(Live\s?(Flex|Beam|Buds)\s?3|Tour\s?Pro\s?(2|3))", O);

    static readonly (Regex Rx, DeviceKind Kind)[] NameHeuristics =
    {
        (new(@"buds|pods|\btws\b|earbud|true\s?wireless|\bear\s?\(|\bT\d{2}\b", O), DeviceKind.Earbuds),
        (new(@"headset", O), DeviceKind.Headset),
        (new(@"headphone|over-?ear|on-?ear", O), DeviceKind.Headphones),
        (new(@"neckband|neck\s?band", O), DeviceKind.Neckband),
        (new(@"speaker|soundbar|\bboom|party", O), DeviceKind.Speaker),
        (new(@"keyboard|\bkeys\b|\bkb\b", O), DeviceKind.Keyboard),
        (new(@"mouse|mice|trackball|trackpad", O), DeviceKind.Mouse),
        (new(@"controller|gamepad|joystick|joy-?con", O), DeviceKind.Gamepad),
        (new(@"\bpen\b|stylus", O), DeviceKind.Pen),
        (new(@"watch|\bband\b", O), DeviceKind.Watch),
    };

    static readonly Regex LePrefix = new(@"^LE[_\-\s]", O);

    /// <summary>Name without the "LE_" prefix some vendors add to the Bluetooth LE side of a dual-mode device.</summary>
    public static string NormalizeName(string? name) => LePrefix.Replace((name ?? "").Trim(), "");

    public static DeviceProfile Match(string name, DeviceKind hint)
    {
        name = NormalizeName(name); // Sony & co. expose the LE side as "LE_<name>"
        foreach (var b in Brands)
        {
            if (!b.Rx.IsMatch(name)) continue;
            foreach (var r in b.Rules)
            {
                if (!r.Rx.IsMatch(name)) continue;
                var kind = r.Kind;
                return Build(b.Name, Model(name, b), kind, r.Case, name, b.Color);
            }
            // Brand recognised, no rule: trust the radio class.
            var k = hint != DeviceKind.Unknown ? hint : Heuristic(name);
            return Build(b.Name, Model(name, b), k, k == DeviceKind.Earbuds, name, b.Color);
        }

        var kindG = hint != DeviceKind.Unknown ? hint : Heuristic(name);
        // Radio class "headphones" + earbud-like name -> earbuds (most TWS report themselves as headphones).
        if (kindG is DeviceKind.Headphones or DeviceKind.Headset && Heuristic(name) == DeviceKind.Earbuds)
            kindG = DeviceKind.Earbuds;
        return Build("", name, kindG, kindG == DeviceKind.Earbuds, name, null);
    }

    static DeviceProfile Build(string brand, string model, DeviceKind kind, bool hasCase, string name, Color? color)
    {
        bool audio = kind is DeviceKind.Earbuds or DeviceKind.Headphones or DeviceKind.Headset or DeviceKind.Neckband;
        var buds = BudsSource.None;
        if (brand is "Apple" or "Beats")
        {
            if (kind is DeviceKind.Earbuds or DeviceKind.Headphones or DeviceKind.Neckband) buds = BudsSource.AppleContinuity;
        }
        else if (kind == DeviceKind.Earbuds)
        {
            buds = BudsSource.FastPair; // Google Fast Pair battery notification (JBL, Sony, Pixel, Nothing, Xiaomi…)
        }

        return new DeviceProfile
        {
            Brand = brand,
            Model = model,
            Kind = kind,
            HasCase = hasCase && kind == DeviceKind.Earbuds,
            Anc = audio && AncRx.IsMatch(name),
            SmartCase = brand == "JBL" && JblSmartCase.IsMatch(name),
            Buds = buds,
            BrandColor = color,
        };
    }

    static string Model(string name, Brand b)
    {
        var prefix = b.StripPrefix ?? b.Name;
        var m = Regex.Replace(name, @"^\s*" + Regex.Escape(prefix) + @"[\s\-_]*", "", O).Trim();
        return m.Length == 0 ? name.Trim() : m;
    }

    public static DeviceKind Heuristic(string name)
    {
        foreach (var (rx, kind) in NameHeuristics)
            if (rx.IsMatch(name)) return kind;
        return DeviceKind.Unknown;
    }

    /// <summary>Text under the device name: brand · model features.</summary>
    public static string Features(DeviceProfile p)
    {
        var parts = new List<string>(4);
        if (p.Brand.Length > 0) parts.Add(p.Brand);
        if (p.Anc) parts.Add("ANC");
        if (p.SmartCase) parts.Add("Smart Case");
        if (p.Kind == DeviceKind.Earbuds && p.Brand.Length > 0) parts.Add("TWS");
        return string.Join(" · ", parts);
    }
}

/// <summary>Apple / Beats model identifiers from the proximity-pairing advertisement.</summary>
internal static class AppleModels
{
    static readonly Dictionary<ushort, (string Name, string Family, DeviceKind Kind)> Map = new()
    {
        [0x0220] = ("AirPods", "airpods", DeviceKind.Earbuds),
        [0x0F20] = ("AirPods 2", "airpods", DeviceKind.Earbuds),
        [0x1320] = ("AirPods 3", "airpods", DeviceKind.Earbuds),
        [0x0E20] = ("AirPods Pro", "airpods pro", DeviceKind.Earbuds),
        [0x1420] = ("AirPods Pro 2", "airpods pro", DeviceKind.Earbuds),
        [0x2420] = ("AirPods Pro 2 (USB-C)", "airpods pro", DeviceKind.Earbuds),
        [0x0A20] = ("AirPods Max", "airpods max", DeviceKind.Headphones),
        [0x0320] = ("Powerbeats3", "powerbeats", DeviceKind.Neckband),
        [0x0B20] = ("Powerbeats Pro", "powerbeats pro", DeviceKind.Earbuds),
        [0x0520] = ("BeatsX", "beatsx", DeviceKind.Neckband),
        [0x1020] = ("Beats Flex", "beats flex", DeviceKind.Neckband),
        [0x0620] = ("Beats Solo3", "beats solo", DeviceKind.Headphones),
        [0x0C20] = ("Beats Solo Pro", "beats solo", DeviceKind.Headphones),
        [0x0920] = ("Beats Studio3", "beats studio", DeviceKind.Headphones),
        [0x1720] = ("Beats Studio Pro", "beats studio pro", DeviceKind.Headphones),
        [0x1120] = ("Beats Studio Buds", "beats studio buds", DeviceKind.Earbuds),
        [0x1620] = ("Beats Studio Buds+", "beats studio buds", DeviceKind.Earbuds),
        [0x1220] = ("Beats Fit Pro", "beats fit pro", DeviceKind.Earbuds),
    };

    public static string? Name(ushort id) => Map.TryGetValue(id, out var m) ? m.Name : null;

    /// <summary>Does the Windows device name plausibly belong to this advertised model?</summary>
    public static int Affinity(string deviceName, string? modelName)
    {
        if (modelName is null) return 0;
        var entry = Map.Values.FirstOrDefault(v => v.Name == modelName);
        if (entry.Family is null) return 0;
        var n = deviceName.ToLowerInvariant();
        if (!n.Contains(entry.Family)) return -10;
        if (entry.Family == "airpods" && (n.Contains("pro") || n.Contains("max"))) return -10;
        return 15;
    }
}
