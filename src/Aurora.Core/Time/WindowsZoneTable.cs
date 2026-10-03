namespace Aurora.Core.Time;

/// <summary>
/// The IANA-to-Windows time zone correspondence, as data.
/// </summary>
/// <remarks>
/// Derived from the Unicode CLDR <c>windowsZones.xml</c> supplemental mapping, which is the same
/// table .NET consults through ICU. It is embedded rather than read from the platform because
/// Aurora builds with <c>InvariantGlobalization</c>: ICU is not loaded, so
/// <see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId(string, out string?)"/> and its inverse
/// return <see langword="false"/> on every machine, and <c>FindSystemTimeZoneById</c> on Windows
/// sees only the registry's Windows ids.
/// <para>
/// Embedding it also makes the answer the same on every machine and in every run, which a lookup
/// through whichever ICU the host happens to ship would not be. <see cref="AuroraTimeZones"/> still
/// asks the platform first, so a machine that does have ICU is believed before this table is
/// consulted; this is the floor, not the ceiling.
/// </para>
/// <para>
/// <b>Format.</b> One Windows zone per entry: <c>Windows Id=primary-iana other-iana …</c>. The
/// first IANA id is CLDR's territory <c>001</c> default and is the one a Windows id canonicalises
/// to — under its current tzdata name where tzdata has since renamed it, which
/// <c>AuroraTimeZones.Renamed</c> lists; the rest are every other IANA id — including tzdata's compatibility links — that CLDR maps
/// onto the same Windows zone. A line beginning with whitespace continues the one above it, which
/// is only so the wide entries stay readable.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> tzdata's four unqualified aliases — <c>CET</c>, <c>EET</c>,
/// <c>MET</c> and <c>WET</c> — have no CLDR mapping, so on Windows they are refused by name rather
/// than guessed at. They resolve normally on Unix, where the platform knows them.
/// </para>
/// </remarks>
internal static class WindowsZoneTable
{
    /// <summary>CLDR's mapping, verbatim.</summary>
    internal const string Entries =
        """
        AUS Central Standard Time=Australia/Darwin Australia/North
        AUS Eastern Standard Time=Australia/Sydney Australia/ACT Australia/Canberra Australia/Melbourne
            Australia/NSW Australia/Victoria
        Afghanistan Standard Time=Asia/Kabul
        Alaskan Standard Time=America/Anchorage America/Juneau America/Metlakatla America/Nome
            America/Sitka America/Yakutat US/Alaska
        Aleutian Standard Time=America/Adak America/Atka US/Aleutian
        Altai Standard Time=Asia/Barnaul
        Arab Standard Time=Asia/Riyadh Asia/Aden Asia/Bahrain Asia/Kuwait Asia/Qatar
        Arabian Standard Time=Asia/Dubai Asia/Muscat Etc/GMT-4
        Arabic Standard Time=Asia/Baghdad
        Argentina Standard Time=America/Buenos_Aires America/Argentina/Buenos_Aires
            America/Argentina/Catamarca America/Argentina/ComodRivadavia America/Argentina/Cordoba
            America/Argentina/Jujuy America/Argentina/La_Rioja America/Argentina/Mendoza
            America/Argentina/Rio_Gallegos America/Argentina/Salta America/Argentina/San_Juan
            America/Argentina/San_Luis America/Argentina/Tucuman America/Argentina/Ushuaia
            America/Catamarca America/Cordoba America/Jujuy America/Mendoza America/Rosario
        Astrakhan Standard Time=Europe/Astrakhan Europe/Ulyanovsk
        Atlantic Standard Time=America/Halifax America/Glace_Bay America/Goose_Bay America/Moncton
            America/Thule Atlantic/Bermuda Canada/Atlantic
        Aus Central W. Standard Time=Australia/Eucla
        Azerbaijan Standard Time=Asia/Baku
        Azores Standard Time=Atlantic/Azores America/Scoresbysund
        Bahia Standard Time=America/Bahia
        Bangladesh Standard Time=Asia/Dhaka Asia/Dacca Asia/Thimbu Asia/Thimphu
        Belarus Standard Time=Europe/Minsk
        Bougainville Standard Time=Pacific/Bougainville
        Canada Central Standard Time=America/Regina America/Swift_Current Canada/Saskatchewan
        Cape Verde Standard Time=Atlantic/Cape_Verde Etc/GMT+1
        Caucasus Standard Time=Asia/Yerevan
        Cen. Australia Standard Time=Australia/Adelaide Australia/Broken_Hill Australia/South
            Australia/Yancowinna
        Central America Standard Time=America/Guatemala America/Belize America/Costa_Rica
            America/El_Salvador America/Managua America/Tegucigalpa Etc/GMT+6 Pacific/Galapagos
        Central Asia Standard Time=Asia/Bishkek Antarctica/Vostok Asia/Urumqi Etc/GMT-6 Indian/Chagos
        Central Brazilian Standard Time=America/Cuiaba America/Campo_Grande
        Central Europe Standard Time=Europe/Budapest Europe/Belgrade Europe/Bratislava Europe/Ljubljana
            Europe/Podgorica Europe/Prague Europe/Tirane
        Central European Standard Time=Europe/Warsaw Europe/Sarajevo Europe/Skopje Europe/Zagreb Poland
        Central Pacific Standard Time=Pacific/Guadalcanal Antarctica/Casey Etc/GMT-11 Pacific/Efate
            Pacific/Kosrae Pacific/Noumea Pacific/Pohnpei Pacific/Ponape
        Central Standard Time=America/Chicago America/Indiana/Knox America/Indiana/Tell_City
            America/Knox_IN America/Matamoros America/Menominee America/North_Dakota/Beulah
            America/North_Dakota/Center America/North_Dakota/New_Salem America/Ojinaga
            America/Rainy_River America/Rankin_Inlet America/Resolute America/Winnipeg CST6CDT
            Canada/Central US/Central US/Indiana-Starke
        Central Standard Time (Mexico)=America/Mexico_City America/Bahia_Banderas America/Chihuahua
            America/Merida America/Monterrey Mexico/General
        Chatham Islands Standard Time=Pacific/Chatham NZ-CHAT
        China Standard Time=Asia/Shanghai Asia/Chongqing Asia/Chungking Asia/Harbin Asia/Hong_Kong
            Asia/Macao Asia/Macau Hongkong PRC
        Cuba Standard Time=America/Havana Cuba
        Dateline Standard Time=Etc/GMT+12
        E. Africa Standard Time=Africa/Nairobi Africa/Addis_Ababa Africa/Asmara Africa/Asmera
            Africa/Dar_es_Salaam Africa/Djibouti Africa/Kampala Africa/Mogadishu Antarctica/Syowa
            Etc/GMT-3 Indian/Antananarivo Indian/Comoro Indian/Mayotte
        E. Australia Standard Time=Australia/Brisbane Australia/Lindeman Australia/Queensland
        E. Europe Standard Time=Europe/Chisinau Europe/Tiraspol
        E. South America Standard Time=America/Sao_Paulo Brazil/East
        Easter Island Standard Time=Pacific/Easter Chile/EasterIsland
        Eastern Standard Time=America/New_York America/Detroit America/Indiana/Petersburg
            America/Indiana/Vincennes America/Indiana/Winamac America/Iqaluit
            America/Kentucky/Louisville America/Kentucky/Monticello America/Louisville America/Montreal
            America/Nassau America/Nipigon America/Pangnirtung America/Thunder_Bay America/Toronto
            Canada/Eastern EST5EDT US/Eastern US/Michigan
        Eastern Standard Time (Mexico)=America/Cancun
        Egypt Standard Time=Africa/Cairo Egypt
        Ekaterinburg Standard Time=Asia/Yekaterinburg
        FLE Standard Time=Europe/Kiev Europe/Helsinki Europe/Kyiv Europe/Mariehamn Europe/Riga
            Europe/Sofia Europe/Tallinn Europe/Uzhgorod Europe/Vilnius Europe/Zaporozhye
        Fiji Standard Time=Pacific/Fiji
        GMT Standard Time=Europe/London Atlantic/Canary Atlantic/Faeroe Atlantic/Faroe Atlantic/Madeira
            Eire Europe/Belfast Europe/Dublin Europe/Guernsey Europe/Isle_of_Man Europe/Jersey
            Europe/Lisbon GB GB-Eire Portugal
        GTB Standard Time=Europe/Bucharest Asia/Famagusta Asia/Nicosia Europe/Athens Europe/Nicosia
        Georgian Standard Time=Asia/Tbilisi
        Greenland Standard Time=America/Godthab America/Nuuk
        Greenwich Standard Time=Atlantic/Reykjavik Africa/Abidjan Africa/Accra Africa/Bamako
            Africa/Banjul Africa/Bissau Africa/Conakry Africa/Dakar Africa/Freetown Africa/Lome
            Africa/Monrovia Africa/Nouakchott Africa/Ouagadougou Africa/Timbuktu America/Danmarkshavn
            Atlantic/St_Helena Iceland
        Haiti Standard Time=America/Port-au-Prince
        Hawaiian Standard Time=Pacific/Honolulu Etc/GMT+10 HST Pacific/Johnston Pacific/Rarotonga
            Pacific/Tahiti US/Hawaii
        India Standard Time=Asia/Calcutta Asia/Kolkata
        Iran Standard Time=Asia/Tehran Iran
        Israel Standard Time=Asia/Jerusalem Asia/Tel_Aviv Israel
        Jordan Standard Time=Asia/Amman
        Kaliningrad Standard Time=Europe/Kaliningrad
        Korea Standard Time=Asia/Seoul ROK
        Libya Standard Time=Africa/Tripoli Libya
        Line Islands Standard Time=Pacific/Kiritimati Etc/GMT-14
        Lord Howe Standard Time=Australia/Lord_Howe Australia/LHI
        Magadan Standard Time=Asia/Magadan
        Magallanes Standard Time=America/Punta_Arenas America/Coyhaique
        Marquesas Standard Time=Pacific/Marquesas
        Mauritius Standard Time=Indian/Mauritius Indian/Mahe Indian/Reunion
        Middle East Standard Time=Asia/Beirut
        Montevideo Standard Time=America/Montevideo
        Morocco Standard Time=Africa/Casablanca Africa/El_Aaiun
        Mountain Standard Time=America/Denver America/Boise America/Cambridge_Bay America/Ciudad_Juarez
            America/Edmonton America/Inuvik America/Shiprock America/Yellowknife Canada/Mountain MST7MDT
            Navajo US/Mountain
        Mountain Standard Time (Mexico)=America/Mazatlan Mexico/BajaSur
        Myanmar Standard Time=Asia/Rangoon Asia/Yangon Indian/Cocos
        N. Central Asia Standard Time=Asia/Novosibirsk
        Namibia Standard Time=Africa/Windhoek
        Nepal Standard Time=Asia/Katmandu Asia/Kathmandu
        New Zealand Standard Time=Pacific/Auckland Antarctica/McMurdo Antarctica/South_Pole NZ
        Newfoundland Standard Time=America/St_Johns Canada/Newfoundland
        Norfolk Standard Time=Pacific/Norfolk
        North Asia East Standard Time=Asia/Irkutsk
        North Asia Standard Time=Asia/Krasnoyarsk Asia/Novokuznetsk
        North Korea Standard Time=Asia/Pyongyang
        Omsk Standard Time=Asia/Omsk
        Pacific SA Standard Time=America/Santiago Chile/Continental
        Pacific Standard Time=America/Los_Angeles America/Vancouver Canada/Pacific PST8PDT US/Pacific
        Pacific Standard Time (Mexico)=America/Tijuana America/Ensenada America/Santa_Isabel
            Mexico/BajaNorte
        Pakistan Standard Time=Asia/Karachi
        Paraguay Standard Time=America/Asuncion
        Qyzylorda Standard Time=Asia/Qyzylorda
        Romance Standard Time=Europe/Paris Africa/Ceuta Europe/Brussels Europe/Copenhagen Europe/Madrid
        Russia Time Zone 10=Asia/Srednekolymsk
        Russia Time Zone 11=Asia/Kamchatka Asia/Anadyr
        Russia Time Zone 3=Europe/Samara
        Russian Standard Time=Europe/Moscow Europe/Kirov Europe/Simferopol W-SU
        SA Eastern Standard Time=America/Cayenne America/Belem America/Fortaleza America/Maceio
            America/Paramaribo America/Recife America/Santarem Antarctica/Palmer Antarctica/Rothera
            Atlantic/Stanley Etc/GMT+3
        SA Pacific Standard Time=America/Bogota America/Atikokan America/Cayman America/Coral_Harbour
            America/Eirunepe America/Guayaquil America/Jamaica America/Lima America/Panama
            America/Porto_Acre America/Rio_Branco Brazil/Acre EST Etc/GMT+5 Jamaica
        SA Western Standard Time=America/La_Paz America/Anguilla America/Antigua America/Aruba
            America/Barbados America/Blanc-Sablon America/Boa_Vista America/Curacao America/Dominica
            America/Grenada America/Guadeloupe America/Guyana America/Kralendijk America/Lower_Princes
            America/Manaus America/Marigot America/Martinique America/Montserrat America/Port_of_Spain
            America/Porto_Velho America/Puerto_Rico America/Santo_Domingo America/St_Barthelemy
            America/St_Kitts America/St_Lucia America/St_Thomas America/St_Vincent America/Tortola
            America/Virgin Brazil/West Etc/GMT+4
        SE Asia Standard Time=Asia/Bangkok Antarctica/Davis Asia/Ho_Chi_Minh Asia/Jakarta
            Asia/Phnom_Penh Asia/Pontianak Asia/Saigon Asia/Vientiane Etc/GMT-7 Indian/Christmas
        Saint Pierre Standard Time=America/Miquelon
        Sakhalin Standard Time=Asia/Sakhalin
        Samoa Standard Time=Pacific/Apia
        Sao Tome Standard Time=Africa/Sao_Tome
        Saratov Standard Time=Europe/Saratov
        Singapore Standard Time=Asia/Singapore Asia/Brunei Asia/Kuala_Lumpur Asia/Kuching Asia/Makassar
            Asia/Manila Asia/Ujung_Pandang Etc/GMT-8 Singapore
        South Africa Standard Time=Africa/Johannesburg Africa/Blantyre Africa/Bujumbura Africa/Gaborone
            Africa/Harare Africa/Kigali Africa/Lubumbashi Africa/Lusaka Africa/Maputo Africa/Maseru
            Africa/Mbabane Etc/GMT-2
        South Sudan Standard Time=Africa/Juba
        Sri Lanka Standard Time=Asia/Colombo
        Sudan Standard Time=Africa/Khartoum
        Syria Standard Time=Asia/Damascus
        Taipei Standard Time=Asia/Taipei ROC
        Tasmania Standard Time=Australia/Hobart Antarctica/Macquarie Australia/Currie Australia/Tasmania
        Tocantins Standard Time=America/Araguaina
        Tokyo Standard Time=Asia/Tokyo Asia/Dili Asia/Jayapura Etc/GMT-9 Japan Pacific/Palau
        Tomsk Standard Time=Asia/Tomsk
        Tonga Standard Time=Pacific/Tongatapu
        Transbaikal Standard Time=Asia/Chita
        Turkey Standard Time=Europe/Istanbul Asia/Istanbul Turkey
        Turks And Caicos Standard Time=America/Grand_Turk
        US Eastern Standard Time=America/Indianapolis America/Fort_Wayne America/Indiana/Indianapolis
            America/Indiana/Marengo America/Indiana/Vevay US/East-Indiana
        US Mountain Standard Time=America/Phoenix America/Creston America/Dawson_Creek
            America/Fort_Nelson America/Hermosillo Etc/GMT+7 MST US/Arizona
        UTC=Etc/UTC Etc/GMT Etc/GMT+0 Etc/GMT-0 Etc/GMT0 Etc/Greenwich Etc/UCT Etc/Universal Etc/Zulu
            GMT GMT+0 GMT-0 GMT0 Greenwich UCT UTC Universal Zulu
        UTC+12=Etc/GMT-12 Kwajalein Pacific/Funafuti Pacific/Kwajalein Pacific/Majuro Pacific/Nauru
            Pacific/Tarawa Pacific/Wake Pacific/Wallis
        UTC+13=Etc/GMT-13 Pacific/Enderbury Pacific/Fakaofo Pacific/Kanton
        UTC-02=Etc/GMT+2 America/Noronha Atlantic/South_Georgia Brazil/DeNoronha
        UTC-08=Etc/GMT+8 Pacific/Pitcairn
        UTC-09=Etc/GMT+9 Pacific/Gambier
        UTC-11=Etc/GMT+11 Pacific/Midway Pacific/Niue Pacific/Pago_Pago Pacific/Samoa US/Samoa
        Ulaanbaatar Standard Time=Asia/Ulaanbaatar Asia/Choibalsan Asia/Ulan_Bator
        Venezuela Standard Time=America/Caracas
        Vladivostok Standard Time=Asia/Vladivostok Asia/Ust-Nera
        Volgograd Standard Time=Europe/Volgograd
        W. Australia Standard Time=Australia/Perth Australia/West
        W. Central Africa Standard Time=Africa/Lagos Africa/Algiers Africa/Bangui Africa/Brazzaville
            Africa/Douala Africa/Kinshasa Africa/Libreville Africa/Luanda Africa/Malabo Africa/Ndjamena
            Africa/Niamey Africa/Porto-Novo Africa/Tunis Etc/GMT-1
        W. Europe Standard Time=Europe/Berlin Arctic/Longyearbyen Atlantic/Jan_Mayen Europe/Amsterdam
            Europe/Andorra Europe/Busingen Europe/Gibraltar Europe/Luxembourg Europe/Malta Europe/Monaco
            Europe/Oslo Europe/Rome Europe/San_Marino Europe/Stockholm Europe/Vaduz Europe/Vatican
            Europe/Vienna Europe/Zurich
        W. Mongolia Standard Time=Asia/Hovd
        West Asia Standard Time=Asia/Tashkent Antarctica/Mawson Asia/Almaty Asia/Aqtau Asia/Aqtobe
            Asia/Ashgabat Asia/Ashkhabad Asia/Atyrau Asia/Dushanbe Asia/Oral Asia/Qostanay
            Asia/Samarkand Etc/GMT-5 Indian/Kerguelen Indian/Maldives
        West Bank Standard Time=Asia/Hebron Asia/Gaza
        West Pacific Standard Time=Pacific/Port_Moresby Antarctica/DumontDUrville Etc/GMT-10
            Pacific/Chuuk Pacific/Guam Pacific/Saipan Pacific/Truk Pacific/Yap
        Yakutsk Standard Time=Asia/Yakutsk Asia/Khandyga
        Yukon Standard Time=America/Whitehorse America/Dawson Canada/Yukon
        """;
}
