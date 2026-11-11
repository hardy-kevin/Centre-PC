namespace CentrePC.Core;

/// <summary>
/// Thèmes par défaut et leurs mots-clés (français + anglais).
/// Couvre l'usage personnel, le gaming, la bureautique, le développement, l'administration système
/// et les principaux métiers. Tout est modifiable dans Réglages > Thèmes, et exportable.
/// Les mots-clés sont comparés au nom de l'appli, puis à sa cible, son dossier du menu Démarrer et son éditeur.
/// Le mot-clé le plus long (le plus précis) l'emporte ; un « s » final est toléré (pluriel).
/// </summary>
public static class DefaultThemes
{
    /// <summary>Augmenter quand la liste change : les nouveautés sont fusionnées dans les configs existantes.</summary>
    public const int Version = 7;

    /// <summary>Anciens noms de thèmes (versions précédentes) → nouveau nom.</summary>
    public static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Internet et communication"] = "Communication",
        ["Multimédia"] = "Vidéo et streaming",
        ["Outils système"] = "Utilitaires",
    };

    /// <summary>
    /// Mots-clés retirés ou déplacés depuis une version précédente (version → thème → mots-clés).
    /// Appliqué une seule fois lors de la mise à niveau : un mot-clé ajouté ensuite par l'utilisateur n'est pas touché.
    /// </summary>
    public static readonly Dictionary<int, Dictionary<string, string[]>> Removed = new()
    {
        [4] = new(StringComparer.OrdinalIgnoreCase)
        {
            // « Cartes » (application Windows retirée) et Power Automate (automatisation) n'étaient pas de la bureautique
            ["Bureautique"] = new[] { "Power Automate", "Cartes", "Maps" },
            // « Exécuter » seul attrapait des noms comme « Exécuter en tant que… » : remplacé par un nom exact
            ["Outils Windows"] = new[] { "Exécuter" },
        },
        [5] = new(StringComparer.OrdinalIgnoreCase)
        {
            // Gestionnaires de mods, extensions et compagnons de jeu : catégorie « Outils pour les jeux »
            ["Lanceurs de jeux"] = new[] { "CurseForge", "WowUp", "Overwolf", "Mod Manager", "Vortex", "Mod Organizer", "r2modman",
                                           "Thunderstore", "=PSApp", "=PS App" },
            ["Jeux"] = new[] { "Content Manager" },
            ["Matériel et pilotes"] = new[] { "SimHub" },
        },
        [7] = new(StringComparer.OrdinalIgnoreCase)
        {
            // « Files » (application de gestion de fichiers) attrapait « Program Files » : nom exact seulement
            ["Utilitaires"] = new[] { "Files" },
            // La Game Bar est un outil (capture, performances), pas un jeu
            ["Jeux"] = new[] { "Game Bar", "Xbox Game Bar" },
        },
    };

    /// <summary>
    /// Catégories créées automatiquement dès qu'une application en relève (mots-clés, description, sources en ligne)
    /// et qu'aucune catégorie existante ne convient. Tant qu'elles sont vides, elles n'apparaissent nulle part.
    /// After : catégorie après laquelle elle s'insère. Une catégorie supprimée par l'utilisateur n'est pas recréée.
    /// </summary>
    public static readonly (Category cat, string after)[] Catalog =
    {
        (C("Outils pour les jeux", "\uE7FC", "#A855F7",
            "CurseForge", "WowUp", "Overwolf", "Mod Manager", "Vortex", "Mod Organizer", "r2modman", "Thunderstore", "Content Manager",
            "SimHub", "Crew Chief", "=Medal", "Medal.tv", "Outplayed", "=Blitz", "Porofessor", "Mobalytics", "Raider.IO", "WeakAuras Companion",
            "WarcraftLogs", "Warcraft Logs", "TradeSkillMaster", "Mod Loader", "Fabric Installer", "Forge Installer", "OptiFine",
            "Nexus Mods", "Wabbajack", "ReShade", "Special K", "Cheat Engine", "WeMod", "Lossless Scaling", "FPS Monitor",
            "Steam Achievement Manager", "Game Helper", "Game Bar", "Xbox Game Bar"), "Lanceurs de jeux"),
        (C("Santé et sport", "\uE95E", "#22C55E",
            "Strava", "Garmin Connect", "Garmin Express", "Zwift", "Fitbit", "Polar Flowsync", "Suunto", "MyFitnessPal", "Fitness"), "Éducation"),
        (C("Voyages et transports", "\uE707", "#0EA5E9",
            "Google Earth Pro", "Waze", "SNCF Connect", "Trainline", "Booking", "Airbnb", "TripAdvisor", "Flightradar24", "Garmin BaseCamp",
            "Garmin Explore", "TomTom"), "Éducation"),
        (C("Lecture et bandes dessinées", "\uE8F1", "#F97316",
            "Kobo", "Thorium Reader", "Readium", "ComicRack", "YACReader", "CDisplayEx", "Comixology", "Izneo", "Webtoon", "Audible"), "Éducation"),
        (C("Maison connectée", "\uE80F", "#84CC16",
            "Home Assistant", "Philips Hue", "Hue Sync", "Google Home", "SmartThings", "Tuya", "Smart Life", "Somfy", "Netatmo", "Domoticz",
            "Jeedom", "Homey", "Nanoleaf", "Govee", "LIFX", "Tapo", "Domotique"), "Matériel et pilotes"),
        // Scripts personnels (.bat, .ps1, .py…) : reconnus par leur cible, pas par des mots-clés
        (C(AppKinds.ScriptsCategory, "\uE943", "#64748B"), "Utilitaires"),
    };

    /// <summary>
    /// Mots-clés trop généraux pour décider seuls (« Launcher » : « Adobe Redeem Launcher » n'est pas un lanceur de jeux).
    /// Ils ne servent qu'en dernier recours, quand aucun autre indice (éditeur, description, sources en ligne, applications voisines) n'a suffi.
    /// </summary>
    public static readonly HashSet<string> WeakKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Launcher", "Windows", "Tools", "Utility", "Utilitaire", "Server", "Serveur", "Developer", "Développeur", "SDK", "IDE",
        "Security", "Sécurité", "Finance", "Education", "Éducation", "Learning", "Formation", "Photo", "Dessin", "Bureautique",
        "Backup", "Sauvegarde", "Driver", "Pilote", "Printer", "Imprimante", "CAD", "CAO", "Fitness", "Domotique"
    };

    public static (Category cat, string after) CatalogEntry(string name) =>
        Catalog.FirstOrDefault(c => c.cat.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static Category C(string name, string glyph, string color, params string[] kw) =>
        new() { Name = name, Glyph = glyph, Color = color, Keywords = kw.ToList() };

    public static List<Category> Build() => new()
    {
        C("Jeux", "", "#7C5CFF",
            // Liens et dossiers de bibliothèques de jeux
            "steam://rungameid", @"steamapps\common", "com.epicgames.launcher://apps", "battlenet://", "uplay://", "origin2://",
            "goggalaxy://", "xbox://", "ms-xbl-", @"Riot Games\", @"Epic Games\", @"Ubisoft Game Launcher\games", "GOG Games", "EA Games",
            "XboxGames", "SteamLibrary", "Early Access", "Dedicated Server",
            // Jeux et licences connus
            "World of Warcraft", "WoW", "Diablo", "Overwatch", "Hearthstone", "StarCraft", "Warcraft", "Call of Duty", "League of Legends",
            "Valorant", "Fortnite", "Minecraft", "Rocket League", "Counter-Strike", "CS2", "Dota", "Apex Legends", "Dofus", "Wakfu",
            "Star Citizen", "Elden Ring", "GTA", "Grand Theft Auto", "Red Dead", "Cyberpunk", "The Sims", "Les Sims", "Assetto Corsa",
            "iRacing", "Rennsport", "rFactor", "Le Mans Ultimate", "Automobilista", "BeamNG", "F1", "Forza", "Gran Turismo", "Euro Truck",
            "American Truck", "Flight Simulator", "DCS World", "X-Plane", "War Thunder", "World of Tanks", "World of Warships",
            "Path of Exile", "Lost Ark", "Genshin", "Honkai", "Wuthering Waves", "Albion Online", "WorldBox", "Soulframe", "Warframe",
            "skate.", "Ark Survival", "Chained Together", "SteamVR", "Rust", "Palworld", "Helldivers", "Baldur", "Sea of Thieves",
            "Destiny", "Halo", "Battlefield", "Need for Speed", "Trackmania", "Snowrunner", "Satisfactory", "Factorio", "Valheim",
            "Terraria", "Stardew", "Hollow Knight", "Roblox", "osu!", "Guild Wars", "Final Fantasy", "Black Desert", "New World",
            "Throne and Liberty", "Marvel Rivals", "The Finals", "PUBG", "Escape from Tarkov", "Squad", "Arma", "DayZ", "Off The Grid",
            "Hogwarts", "Starfield", "Fallout", "Skyrim", "The Elder Scrolls", "Witcher", "Mass Effect", "Dragon Age", "Assassin's Creed",
            "Far Cry", "Rainbow Six", "The Crew", "Anno", "Civilization", "Total War", "Age of Empires", "Crusader Kings",
            "Hearts of Iron", "Europa Universalis", "Stellaris", "Cities: Skylines", "Planet Coaster", "Football Manager", "EA SPORTS FC",
            "FIFA", "NBA 2K", "Mortal Kombat", "Street Fighter", "Tekken", "Resident Evil", "Monster Hunter", "Dark Souls", "Sekiro",
            "Lethal Company", "Phasmophobia", "Among Us", "Fall Guys", "Brawlhalla", "Paladins", "Smite", "Deadlock", "Overwatch 2"),

        C("Lanceurs de jeux", "", "#C026D3",
            "Steam", "Epic Games Launcher", "EpicGamesLauncher", "Battle.net", "EA app", "EA", "Origin", "Ubisoft Connect", "Ubisoft",
            "GOG Galaxy", "GOG", "Rockstar Games Launcher", "Riot Client", "Client Riot", "Ankama Launcher", "Ankama", "RSI Launcher",
            "Amazon Games", "Xbox", "Xbox App", "Playnite", "Heroic", "itch", "Prism Launcher",
            "Minecraft Launcher", "Paradox Launcher", "BlueStacks", "LDPlayer", "MuMu Player", "NoxPlayer", "Nintendo", "Ryujinx", "Yuzu",
            "Dolphin", "RetroArch", "PCSX2", "RPCS3", "DuckStation", "PPSSPP", "Cemu", "Steam Big Picture", "GeForce NOW", "Boosteroid",
            "Xbox Cloud Gaming", "PS Remote Play", "PlayStation", "PlayStation App", "PlayStation Plus", "Sony Interactive Entertainment", "Moonlight", "Sunshine", "Launcher", "Game Launcher"),

        C("Internet", "", "#3B82F6",
            "Google Chrome", "Chrome", "Firefox", "Mozilla Firefox", "Microsoft Edge", "Edge", "Opera", "Opera GX", "Brave", "Vivaldi",
            "Tor Browser", "Zen Browser", "Arc Browser", "Thorium", "LibreWolf", "Waterfox", "Floorp", "Chromium", "Safari",
            "qBittorrent", "uTorrent", "BitTorrent", "Transmission", "Deluge", "FileZilla", "Cyberduck", "JDownloader",
            "Internet Download Manager", "Free Download Manager", "Motrix", "yt-dlp", "4K Video Downloader", "Actualités", "News",
            "Feedly", "RSS", "Pocket", "Navigateur", "Browser"),

        C("Communication", "", "#0EA5E9",
            "Discord", "TeamSpeak", "Mumble", "WhatsApp", "Telegram", "Signal", "Messenger", "Skype", "Microsoft Teams", "Teams", "Zoom",
            "Slack", "Webex", "Google Meet", "GoTo Meeting", "Jitsi", "Rocket.Chat", "Mattermost", "Guilded", "Viber", "WeChat", "Outlook", "Thunderbird", "Courrier", "Mail", "Proton Mail", "Mailbird", "eM Client", "Mobile connecté",
            "Phone Link", "Votre téléphone", "Intel Unison", "KDE Connect", "Pushbullet", "3CX", "Zoiper", "MicroSIP", "Ring Central",
            "Twitter", "Facebook", "Instagram", "TikTok", "Reddit", "LinkedIn", "Bluesky", "Mastodon", "Snapchat"),

        C("IA et assistants", "", "#A78BFA",
            "Claude", "ChatGPT", "OpenAI", "Copilot", "Microsoft Copilot", "Gemini", "Perplexity", "Mistral", "Le Chat", "DeepSeek",
            "Ollama", "LM Studio", "GPT4All", "Msty", "AnythingLLM", "Open WebUI", "Stable Diffusion", "ComfyUI", "Automatic1111",
            "Fooocus", "Midjourney", "Leonardo", "Whisper", "Cursor", "Windsurf", "Codeium", "Tabnine", "Poe", "Character.AI"),

        C("Musique et audio", "", "#EC4899",
            "Spotify", "Deezer", "Apple Music", "iTunes", "Tidal", "Qobuz", "Amazon Music", "YouTube Music", "SoundCloud", "Foobar",
            "foobar2000", "MusicBee", "AIMP", "Winamp", "Audacity", "Reaper", "FL Studio", "Ableton", "Ableton Live", "Cubase", "Pro Tools",
            "Studio One", "Bitwig", "LMMS", "GarageBand", "Ocenaudio", "Audition", "Adobe Audition", "Voicemeeter", "VB-Audio",
            "Equalizer APO", "Peace Equalizer", "SteelSeries Sonar", "Nahimic", "Dolby Access", "Dolby", "DTS Sound", "Sonic Studio",
            "EarTrumpet", "Enregistreur vocal", "Sound Recorder", "Magnétophone", "Podcast", "Krisp", "NVIDIA Broadcast", "Mixxx",
            "Serato", "Rekordbox", "Traktor", "MuseScore", "Guitar Pro", "Sibelius", "Shazam", "Musique", "Music", "Audio"),

        C("Vidéo et streaming", "", "#EF4444",
            "VLC", "MPC-HC", "MPC-BE", "PotPlayer", "mpv", "KMPlayer", "GOM Player", "Lecteur multimédia", "Media Player",
            "Windows Media Player", "Lecteur Windows Media", "Films et TV", "Movies & TV", "Plex", "Kodi", "Jellyfin", "Emby", "Stremio",
            "Netflix", "Disney", "Prime Video", "Apple TV", "Canal+", "Molotov", "Crunchyroll", "YouTube", "Twitch",
            "OBS", "OBS Studio", "Streamlabs", "XSplit", "Streamer.bot", "StreamDeck", "Premiere", "Premiere Pro", "After Effects",
            "DaVinci Resolve", "CapCut", "Shotcut", "Kdenlive", "OpenShot", "Vegas Pro", "Filmora", "Clipchamp", "Microsoft Clipchamp",
            "Avidemux", "HandBrake", "MKVToolNix", "FFmpeg", "Topaz Video", "Camtasia", "Bandicam", "Fraps", "ShadowPlay", "Medal",
            "Outplayed", "Caméra", "Camera", "Webcam", "Logi Capture", "Elgato Camera Hub", "Vidéo", "Video"),

        C("Photo et graphisme", "", "#F97316",
            "Adobe Inc.", "Adobe Systems",
            "Photos", "Paint", "Paint.NET", "Paint 3D", "Photoshop", "Lightroom", "Illustrator", "InDesign", "Adobe Express", "Acrobat Pro",
            "GIMP", "Krita", "Inkscape", "Affinity", "Affinity Photo", "Affinity Designer", "Affinity Publisher", "Canva", "Figma", "Sketch",
            "Darktable", "RawTherapee", "Capture One", "DxO", "Luminar", "Topaz Photo", "IrfanView", "XnView", "FastStone", "ImageGlass",
            "Honeyview", "Blender", "Cinema 4D", "Maya", "3ds Max", "ZBrush", "Substance", "Houdini", "Marvelous Designer", "Clip Studio",
            "Procreate", "MediBang", "FireAlpaca", "Aseprite", "Pixelorama", "Scribus", "CorelDRAW", "Corel", "Snipping Tool",
            "Outil Capture", "ShareX", "Greenshot", "Lightshot", "Flameshot", "Snagit", "Lunacy", "Penpot", "Photo", "Dessin"),

        C("Bureautique", "", "#F59E0B",
            "Microsoft Word", "Word", "Microsoft Excel", "Excel", "Microsoft Access", "Microsoft Project", "Microsoft Publisher", "PowerPoint", "OneNote", "Microsoft 365", "Office", "Publisher", "Visio",
            "LibreOffice", "OpenOffice", "OnlyOffice", "WPS Office", "SoftMaker", "Google Docs", "Acrobat", "Adobe Reader", "Acrobat Reader",
            "PDF", "SumatraPDF", "Foxit", "PDF24", "PDFgear", "Okular", "Xournal", "Notepad++", "Bloc-notes", "Notepad", "Notion",
            "Obsidian", "Evernote", "Joplin", "Logseq", "Typora", "Simplenote", "Pense-bête", "Sticky Notes", "Calendrier", "Calendar",
            "Calculatrice", "Calculator", "Microsoft To Do", "To Do", "Todoist", "TickTick", "Trello", "Asana", "ClickUp",
            "Météo", "Weather", "Horloge", "Clock", "Alarmes", "Kindle", "Calibre", "Adobe Digital Editions",
            "Zotero", "Mendeley", "DeepL", "Antidote", "Grammarly", "LanguageTool", "Dictionnaire", "Scanner", "NAPS2", "Bureautique"),

        C("Gestion et finance", "", "#CA8A04",
            "Sage", "EBP", "Ciel", "QuickBooks", "Xero", "Odoo", "Dolibarr", "SAP", "Cegid", "Quadratus", "Pennylane", "Indy", "Tiime",
            "GnuCash", "HomeBank", "Money Manager", "Grisbi", "Banque", "Bank", "Impôts", "Comptabilité", "Accounting", "Facturation",
            "Invoice", "CRM", "Salesforce", "HubSpot", "ERP", "Paie", "Payroll", "Silae", "Kelio", "Lucca", "PayFit", "TeamViewer Tensor",
            "Power BI Report Builder", "Docusign", "Yousign", "Boursorama", "Trade Republic", "Interactive Brokers", "TradingView",
            "MetaTrader", "Ledger Live", "Exodus", "Finance"),

        C("Ingénierie et CAO", "", "#B45309",
            "AutoCAD", "Autodesk", "Revit", "Inventor", "Fusion 360", "SolidWorks", "CATIA", "Creo", "Solid Edge",
            "SketchUp", "FreeCAD", "LibreCAD", "DraftSight", "BricsCAD", "ArchiCAD", "Vectorworks", "Rhino", "Grasshopper", "Tekla",
            "ANSYS", "Abaqus", "COMSOL", "LTspice", "KiCad", "Eagle", "Altium", "EasyEDA", "Proteus", "Multisim", "Quartus", "Vivado",
            "MPLAB", "STM32CubeIDE", "TIA Portal", "Codesys", "Cura", "UltiMaker Cura", "PrusaSlicer", "OrcaSlicer", "Bambu Studio",
            "Chitubox", "Lychee", "Meshmixer", "QGIS", "ArcGIS", "Google Earth", "CAO", "CAD"),

        C("Science et données", "", "#6366F1",
            "MATLAB", "Simulink", "Mathematica", "Maple", "Scilab", "Octave", "GNU Octave", "RStudio", "SPSS", "SAS", "Stata",
            "JASP", "jamovi", "Origin", "OriginPro", "Minitab", "Anaconda", "Anaconda Navigator", "Jupyter", "JupyterLab", "Spyder",
            "Orange", "KNIME", "RapidMiner", "Tableau", "Power BI", "Power BI Desktop", "Qlik", "Looker", "Metabase", "GeoGebra",
            "LaTeX", "MiKTeX", "TeXstudio", "TeXworks", "Overleaf", "LyX", "ImageJ", "Fiji", "ChemDraw", "Avogadro", "PyMOL",
            "Stellarium", "Celestia", "Wolfram"),

        C("Éducation", "", "#FB7185",
            "Anki", "Duolingo", "Babbel", "Rosetta Stone", "Mosa Lingua", "Scratch", "Khan Academy", "Coursera", "Udemy", "OpenClassrooms",
            "Pronote", "Ecole Directe", "Moodle", "Classroom", "Google Classroom", "Kahoot", "Quizlet", "Typing Club", "Klavaro",
            "TuxType", "TuxMath", "GCompris", "Lexibook", "Éducation", "Education", "Learning", "Formation"),

        C("Développement", "", "#10B981",
            "Visual Studio Code", "VS Code", "Visual Studio", "Visual Studio Installer", "JetBrains", "IntelliJ", "IntelliJ IDEA",
            "PyCharm", "WebStorm", "PhpStorm", "Rider", "CLion", "GoLand", "DataGrip", "RubyMine", "Android Studio", "Eclipse", "NetBeans",
            "Sublime Text", "Atom", "Zed", "Neovim", "Vim", "Emacs", "Notepad3", "Git", "Git Bash", "Git CMD", "Git GUI", "GitHub Desktop",
            "GitKraken", "SourceTree", "Fork", "TortoiseGit", "TortoiseSVN", "Python", "IDLE", "Node.js", "npm", "Java", "JDK",
            ".NET", "Unity", "Unity Hub", "Unreal", "Unreal Engine", "Godot", "GameMaker", "Arduino", "PlatformIO", "Docker",
            "Docker Desktop", "Podman", "Postman", "Insomnia", "Bruno", "Fiddler", "Charles", "XAMPP", "XAMPP Control Panel", "Bitnami",
            "WAMP", "Laragon", "MAMP", "Apache", "Composer", "PHP", "Symfony", "PostgreSQL", "pgAdmin", "psql", "SQL Shell",
            "Stack Builder", "Application Stack Builder", "MySQL", "MySQL Workbench", "MariaDB", "HeidiSQL", "DBeaver", "SQLite",
            "DB Browser", "MongoDB", "MongoDB Compass", "Redis", "SQL Server Management Studio", "SSMS", "Azure Data Studio",
            "Sources de données ODBC", "ODBC", "Install Additional Tools for Node.js", "Developer", "Développeur", "SDK", "IDE"),

        C("Administration système et réseau", "", "#0891B2",
            "Terminal", "Windows Terminal", "Windows PowerShell", "PowerShell", "PowerShell ISE", "Invite de commandes", "Command Prompt",
            "cmd", "PuTTY", "KiTTY", "MobaXterm", "Termius", "Tabby", "SecureCRT", "mRemoteNG", "Royal TS", "Remote Desktop Manager",
            "Connexion Bureau à distance", "Remote Desktop Connection", "Remote Desktop", "Bureau à distance", "WinSCP", "TeamViewer",
            "AnyDesk", "RustDesk", "Parsec", "Chrome Remote Desktop", "Splashtop", "VNC", "RealVNC", "TightVNC", "UltraVNC",
            "Wireshark", "Nmap", "Zenmap", "Angry IP Scanner", "Advanced IP Scanner", "Hamachi", "LogMeIn", "ZeroTier", "Tailscale",
            "WireGuard", "OpenVPN", "FortiClient", "Cisco", "AnyConnect", "GlobalProtect", "Hyper-V", "Gestionnaire Hyper-V", "VMware",
            "VMware Workstation", "VirtualBox", "Oracle VM", "Proxmox", "WSL", "Ubuntu", "Debian", "Kali", "Windows Subsystem",
            "Windows Sandbox", "Ansible", "Terraform", "Kubernetes", "kubectl", "Lens", "Azure", "AWS", "Google Cloud", "RSAT",
            "Active Directory", "Utilisateurs et ordinateurs Active Directory", "DNS", "DHCP", "Group Policy", "Stratégie de groupe",
            "Sysinternals", "Process Explorer", "Process Monitor", "Autoruns", "TCPView", "PsExec", "Initiateur iSCSI", "iSCSI Initiator",
            "Zabbix", "PRTG", "Nagios", "Grafana", "Veeam", "Snort", "Putty", "Serveur", "Server"),

        C("Sécurité", "", "#65A30D",
            "Sécurité Windows", "Windows Security", "Windows Defender", "Pare-feu", "Firewall", "Malwarebytes", "Kaspersky", "Bitdefender",
            "Avast", "AVG", "Norton", "ESET", "McAfee", "Sophos", "Trend Micro", "Avira", "Panda", "HitmanPro", "AdwCleaner", "Spybot",
            "Bitwarden", "KeePass", "KeePassXC", "1Password", "Dashlane", "LastPass", "NordPass", "Proton Pass", "Authy", "Authenticator",
            "Microsoft Authenticator", "NordVPN", "ProtonVPN", "Proton VPN", "Surfshark", "ExpressVPN", "CyberGhost", "Mullvad", "Windscribe",
            "VeraCrypt", "BitLocker", "Cryptomator", "Gpg4win", "Kleopatra", "Family", "Microsoft Family", "Contrôle parental",
            "Parental Control", "Sécurité", "Security", "Antivirus"),

        C("Cloud et sauvegarde", "", "#38BDF8",
            "OneDrive", "Dropbox", "Google Drive", "iCloud", "Nextcloud", "ownCloud", "pCloud", "MEGA", "MEGAsync",
            "Proton Drive", "Synology", "Synology Drive", "QNAP", "Sauvegarde Windows", "Windows Backup", "Macrium", "Macrium Reflect",
            "AOMEI", "AOMEI Backupper", "Acronis", "EaseUS Todo", "Veeam Agent", "Duplicati", "Cobian", "FreeFileSync", "SyncBack",
            "Syncthing", "Resilio", "rclone", "Backblaze", "Carbonite", "Lecteur de récupération", "Recovery Drive", "Sauvegarde", "Backup"),

        C("Matériel et pilotes", "", "#E11D48",
            "AMD Software", "AMD", "Radeon", "Adrenalin", "AMD Install Manager", "Ryzen Master", "NVIDIA", "NVIDIA App", "GeForce",
            "GeForce Experience", "NVIDIA Control Panel", "Panneau de configuration NVIDIA", "Intel", "Intel Driver", "Intel Graphics",
            "Arc Control", "Realtek", "Realtek Audio Console", "Audio Console", "Logitech", "G HUB", "Logi Options", "Razer", "Synapse",
            "Corsair", "iCUE", "SteelSeries", "SteelSeries GG", "HyperX", "NGENUITY", "ROCCAT", "Wootility", "Glorious Core", "ASUS",
            "Armoury Crate", "MyASUS", "MSI", "MSI Center", "Dragon Center", "MSI Afterburner", "Afterburner", "RivaTuner", "GIGABYTE",
            "Control Center", "ASRock", "Fanatec", "Fanatec Control Panel", "Thrustmaster", "Moza", "Pit House", "Simucube",
            "Logitech G", "Cardo", "Accessoires Xbox", "Xbox Accessories", "DS4Windows", "DSX", "Steam Input", "Meta Horizon", "Oculus",
            "Virtual Desktop", "Pico", "HTC Vive", "Varjo", "HWiNFO", "CPU-Z", "GPU-Z", "HWMonitor", "AIDA64", "CrystalDiskInfo",
            "CrystalDiskMark", "Samsung Magician", "WD Dashboard", "Crucial Storage Executive", "OpenRGB", "SignalRGB", "FanControl",
            "Elgato", "Stream Deck", "Wacom", "XP-Pen", "Huion", "HP Smart", "Epson", "Canon", "Brother", "Imprimante", "Printer",
            "Lenovo Vantage", "Dell Command", "SupportAssist", "HP Support Assistant", "Bluetooth", "Pilote", "Driver", "Install Driver",
            "Device Manager", "Gestionnaire de périphériques"),

        C("Utilitaires", "", "#14B8A6",
            "7-Zip", "7-Zip File Manager", "WinRAR", "PeaZip", "Bandizip", "NanaZip", "CCleaner", "BleachBit", "Revo Uninstaller",
            "IObit", "Windhawk", "PowerToys", "Everything", "Listary", "Flow Launcher", "Raycast", "Wox", "TreeSize", "WinDirStat",
            "WizTree", "SpaceSniffer", "Rufus", "Ventoy", "balenaEtcher", "Etcher", "OneCommander", "Directory Opus",
            "Total Commander", "Double Commander", "Q-Dir", "Ditto", "=Files",
            "ClipboardFusion", "AutoHotkey", "Power Automate", "Power Automate Desktop", "Keypirinha", "f.lux", "Rainmeter", "Wallpaper Engine", "Lively", "TranslucentTB",
            "StartAllBack", "Start11", "ExplorerPatcher", "Open-Shell", "Fences", "Ninite", "UniGetUI", "WingetUI", "Chocolatey", "Scoop",
            "Patch My PC", "SUMo", "Mise à jour PC", "Centre PC", "Ranger le bureau", "Annuler le rangement", "Range Bureau",
            "O&O ShutUp", "Optimizer", "Bulk Rename", "Advanced Renamer", "Duplicate Cleaner", "dupeGuru", "Recuva", "TestDisk", "PhotoRec",
            "MiniTool", "GParted", "Partition Wizard", "EaseUS Partition", "HDD Low Level Format", "Utilitaire", "Utility", "Tools"),

        C("Outils Windows", "", "#475569",
            "Paramètres", "Settings", "Explorateur de fichiers", "File Explorer", "Explorateur Windows", "Panneau de configuration", "Control Panel", "Gestionnaire des tâches", "Task Manager",
            "Planificateur de tâches", "Task Scheduler", "Outils Windows", "Windows Tools", "Administrative Tools", "Outils d'administration",
            "Analyseur de performances", "Performance Monitor", "Moniteur de ressources", "Resource Monitor", "Assistance rapide",
            "Quick Assist", "Prise en main", "Get Started", "Character Map", "Table des caractères", "Clavier visuel", "On-Screen Keyboard",
            "Component Services", "Services de composants", "Computer Management", "Gestion de l'ordinateur", "Gestion des disques",
            "Disk Management", "Configuration du système", "System Configuration", "Informations système", "System Information",
            "Loupe", "Magnify", "Memory Diagnostics", "Diagnostic de la mémoire", "Windows Memory Diagnostic", "Narrateur", "Narrator",
            "=Exécuter", "=Exécuter…", "=Run", "=Cartes", "=Maps", "Windows Maps", "Services", "Steps Recorder", "Enregistreur d'actions", "Accès vocal", "Voice Access", "VoiceAccess",
            "Sous-titres en direct", "Live Captions", "LiveCaptions", "Hub de commentaires", "Feedback Hub", "Observateur d'événements",
            "Event Viewer", "Éditeur du Registre", "Registry Editor", "Regedit", "Défragmenter", "Defragment", "Optimiser les lecteurs",
            "dfrgui", "Nettoyage de disque", "Disk Cleanup", "Microsoft Store", "Windows HDR", "À propos de Windows", "Actions par clic",
            "Click to Do", "Get Help", "Obtenir de l'aide", "Conseils", "PC Manager", "Windows Update", "Centre de mobilité",
            "Mobility Center", "Reconnaissance vocale", "Speech Recognition", "Windows Fax", "Fax et numérisation", "Print Management",
            "Gestion de l'impression", "ODBC Data Sources", "Windows PowerShell ISE", "Redémarrer", "Reboot", "Boot Menu", "bootmenu",
            "Power Automate Troubleshooter", "Troubleshooter", "Utilitaire de résolution des problèmes", "Mixed Reality Portal", "WordPad", "Windows")
    };
}
