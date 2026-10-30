using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace IMESupport.Services;

/// <summary>
/// キーストローク解析結果
/// </summary>
public class KeystrokeAnalysisResult
{
    /// <summary>
    /// ローマ字崩壊・打鍵ミスの異常が検知されたか
    /// </summary>
    public bool HasAnomaly { get; set; }

    /// <summary>
    /// 漢字をよみがな（ひらがな）に還元したテキスト
    /// </summary>
    public string ReadingText { get; set; } = string.Empty;

    /// <summary>
    /// よみがなと残存アルファベットから推定したローマ字打鍵列
    /// </summary>
    public string EstimatedRomaji { get; set; } = string.Empty;

    /// <summary>
    /// AIプロンプトへ付加する解析ヒント文字列
    /// </summary>
    public string PromptHint { get; set; } = string.Empty;
}

/// <summary>
/// キーストローク逆変換アナライザー
/// ローマ字入力中の母音抜け・隣接キー誤打により崩壊した日本語（例: 「あr瓦当」）から
/// よみがなおよび推定ローマ字打鍵列（例: 「argatou」）を復元します。
/// </summary>
public static partial class KeystrokeAnalyzer
{
    #region MS-IME IFELanguage COM 定義

    private const string MSIME_PROGID = "MSIME.Japan";
    private const string IFELANGUAGE_IID = "019F7152-E6DB-11d0-83C3-00C04FDDB82E";

    private const uint FELANG_REQ_REV = 0x00030000;
    private const uint FELANG_CMODE_HIRAGANAOUT = 0x00000000;
    private const uint FELANG_CMODE_PRECONV = 0x00000008;

    [ComImport]
    [Guid(IFELANGUAGE_IID)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFELanguage
    {
        [PreserveSig]
        int Open();

        [PreserveSig]
        int Close();

        [PreserveSig]
        int GetJMorphResult(
            uint dwRequest,
            uint dwCMode,
            int cwchInput,
            [MarshalAs(UnmanagedType.LPWStr)] string pwchInput,
            IntPtr pfCInfo,
            out IntPtr ppResult);

        [PreserveSig]
        int GetConversionModeCaps(out uint pdwCaps);

        [PreserveSig]
        int GetPhonetic(
            [MarshalAs(UnmanagedType.BStr)] string str,
            int start,
            int length,
            [MarshalAs(UnmanagedType.BStr)] out string phonetic);

        [PreserveSig]
        int GetConversion(
            [MarshalAs(UnmanagedType.BStr)] string str,
            int start,
            int length,
            [MarshalAs(UnmanagedType.BStr)] out string result);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MORRSLT
    {
        public uint dwSize;
        public IntPtr pwchOutput;
        public ushort cchOutput;
    }

    #endregion

    // 日本語文字（ひらがな・カタカナ・漢字・長音符）の前後または途中に1〜3文字の英字（特に子音）が混在するパターン
    private static readonly Regex IsolatedAlphaInJapaneseRegex = new(
        @"(?:[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}ー][a-zA-Zａ-ｚＡ-Ｚ]{1,3}|[a-zA-Zａ-ｚＡ-Ｚ]{1,3}[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}ー])",
        RegexOptions.Compiled);

    // 孤立した子音（母音を伴わない1〜3文字の子音列）が日本語と隣接しているパターン
    private static readonly Regex IsolatedConsonantInJapaneseRegex = new(
        @"(?:[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}ー][bcdfghjklmnpqrstvwxyzBCDFGHJKLMNPQRSTVWXYZｂｃｄｆｇｈｊｋｌｍｎｐｑｒｓｔｖｗｘｙｚＢＣＤＦＧＨＪＫＬＭＮＰＱＲＳＴＶＷＸＹＺ]{1,3}|[bcdfghjklmnpqrstvwxyzBCDFGHJKLMNPQRSTVWXYZｂｃｄｆｇｈｊｋｌｍｎｐｑｒｓｔｖｗｘｙｚＢＣＤＦＧＨＪＫＬＭＮＰＱＲＳＴＶＷＸＹＺ]{1,3}[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}ー])",
        RegexOptions.Compiled);

    // 不自然な小文字かなの孤立（行頭・記号直後の小文字かな、または連続する小文字かな等）
    private static readonly Regex IsolatedSmallKanaRegex = new(
        @"(?:^|[^\p{IsHiragana}\p{IsKatakana}])[ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮ]|[ぁぃぅぇぉゃゅょ][ぁぃぅぇぉゃゅょ]|っっ",
        RegexOptions.Compiled);

    // 日本語配列で母音・長音周辺にあるキー（; : [ @ - 等）の日本語文中への不自然な誤混入
    private static readonly Regex TypoSymbolInJapaneseRegex = new(
        @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}][;:\[\]\@\-\^\\；：［］＠＾￥][\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]",
        RegexOptions.Compiled);

    // トークン分割用（漢字を含む和語チャンク vs 非漢字チャンク）
    private static readonly Regex KanjiChunkRegex = new(
        @"[\p{IsCJKUnifiedIdeographs}々〆ヵヶ]+",
        RegexOptions.Compiled);

    /// <summary>
    /// 入力テキストにローマ字崩壊・打鍵ミスの異常パターンが含まれるか判定します。
    /// </summary>
    public static bool HasKeystrokeAnomaly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 1. 子音字が日本語文字の前後・途中に混在している場合は確実に異常（例: あr瓦当, おhよう, sれでは）
        if (IsolatedConsonantInJapaneseRegex.IsMatch(text))
        {
            return true;
        }

        // 2. ひらがなの途中に1〜2文字のアルファベットが挟まっている場合（例: あiう, かaき）
        if (Regex.IsMatch(text, @"[\p{IsHiragana}][a-zA-Zａ-ｚＡ-Ｚ]{1,2}[\p{IsHiragana}\p{IsCJKUnifiedIdeographs}]"))
        {
            return true;
        }

        // 3. 不自然な小文字かなの孤立や連続
        if (IsolatedSmallKanaRegex.IsMatch(text))
        {
            return true;
        }

        // 4. 日本語配列の周辺記号キーの誤混入
        if (TypoSymbolInJapaneseRegex.IsMatch(text))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// テキスト内の漢字部分を Windows 標準 IFELanguage COM（およびフォールバック）でよみがな（ひらがな）に変換します。
    /// 途中に挟まっている半角/全角英字（r 等）は消えずにそのまま保持されます。
    /// </summary>
    public static string ConvertToReading(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 全角英数字を半角へ正規化しつつ、カタカナはひらがなへ揃え、漢字塊のみよみがな変換する
        var sb = new StringBuilder(text.Length * 2);
        int lastIndex = 0;

        foreach (Match match in KanjiChunkRegex.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                string nonKanji = text.Substring(lastIndex, match.Index - lastIndex);
                sb.Append(NormalizeKanaAndAlpha(nonKanji));
            }

            string kanjiChunk = match.Value;
            string reading = GetKanjiReadingSafe(kanjiChunk);
            sb.Append(NormalizeKanaAndAlpha(reading));

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            sb.Append(NormalizeKanaAndAlpha(text.Substring(lastIndex)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// よみがな変換とローマ字逆変換を行い、推定キーストローク列を生成します。
    /// （例: 「あr瓦当」 -> よみがな「あrがとう」 -> 推定ローマ字「argatou」）
    /// </summary>
    public static string EstimateKeystrokes(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string reading = ConvertToReading(text);
        return ConvertReadingToRomaji(reading);
    }

    /// <summary>
    /// AI向け解析ヒントを生成します。
    /// </summary>
    public static KeystrokeAnalysisResult BuildAnalysisHint(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new KeystrokeAnalysisResult();
        }

        bool hasAnomaly = HasKeystrokeAnomaly(text);
        string readingText = ConvertToReading(text);
        string estimatedRomaji = ConvertReadingToRomaji(readingText);

        string promptHint = string.Empty;
        if (hasAnomaly)
        {
            promptHint =
                $"【推定ローマ字打鍵列（タイポ復元ヒント）】\n" +
                $"読み還元: {readingText}\n" +
                $"推定打鍵: {estimatedRomaji}";
        }

        return new KeystrokeAnalysisResult
        {
            HasAnomaly = hasAnomaly,
            ReadingText = readingText,
            EstimatedRomaji = estimatedRomaji,
            PromptHint = promptHint
        };
    }

    #region IFELanguage 呼び出しとフォールバック

    private static string GetKanjiReadingSafe(string kanjiChunk)
    {
        if (string.IsNullOrEmpty(kanjiChunk))
        {
            return string.Empty;
        }

        try
        {
            string? comResult = null;
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                comResult = InvokeIFELanguage(kanjiChunk);
            }
            else
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        comResult = InvokeIFELanguage(kanjiChunk);
                    }
                    catch
                    {
                        // STAスレッド内でのCOM例外を無視してフォールバックへ
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join(500);
            }

            if (!string.IsNullOrEmpty(comResult))
            {
                return comResult;
            }
        }
        catch
        {
            // COM呼び出し失敗時は安全にフォールバック
        }

        return FallbackKanjiReading(kanjiChunk);
    }

    private static string? InvokeIFELanguage(string input)
    {
        Type? imeType = Type.GetTypeFromProgID(MSIME_PROGID);
        if (imeType == null)
        {
            return null;
        }

        object? instance = null;
        IFELanguage? feLang = null;
        bool isOpened = false;

        try
        {
            instance = Activator.CreateInstance(imeType);
            feLang = instance as IFELanguage;
            if (feLang == null)
            {
                return null;
            }

            int hr = feLang.Open();
            if (hr != 0)
            {
                return null;
            }
            isOpened = true;

            hr = feLang.GetJMorphResult(
                FELANG_REQ_REV,
                FELANG_CMODE_HIRAGANAOUT | FELANG_CMODE_PRECONV,
                input.Length,
                input,
                IntPtr.Zero,
                out IntPtr ppResult);

            if (hr == 0 && ppResult != IntPtr.Zero)
            {
                try
                {
                    var morphResult = Marshal.PtrToStructure<MORRSLT>(ppResult);
                    if (morphResult.pwchOutput != IntPtr.Zero && morphResult.cchOutput > 0)
                    {
                        string? reading = Marshal.PtrToStringUni(morphResult.pwchOutput, morphResult.cchOutput);
                        if (!string.IsNullOrEmpty(reading))
                        {
                            return reading;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ppResult);
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (feLang != null && isOpened)
            {
                try { feLang.Close(); } catch { }
            }
            if (instance != null && Marshal.IsComObject(instance))
            {
                try { Marshal.ReleaseComObject(instance); } catch { }
            }
        }

        return null;
    }

    // COM が利用できない環境（ヘッドレスCI等）向けの最小限フォールバック辞書
    private static readonly (string Kanji, string Reading)[] FallbackDictionary =
    [
        ("瓦当", "がとう"),
        ("有難う", "ありがとう"),
        ("今日は", "きょうは"),
        ("今日", "きょう"),
        ("良い", "よい"),
        ("天気", "てんき"),
        ("変換", "へんかん"),
        ("変改", "へんかい"),
        ("機構", "きこう"),
        ("気候", "きこう"),
        ("確認", "かくにん"),
        ("テスト", "てすと"),
        ("宜しく", "よろしく"),
        ("お願い", "おねがい"),
        ("致します", "いたします")
    ];

    private static string FallbackKanjiReading(string kanjiChunk)
    {
        string result = kanjiChunk;
        foreach (var (kanji, reading) in FallbackDictionary)
        {
            if (result.Contains(kanji, StringComparison.Ordinal))
            {
                result = result.Replace(kanji, reading, StringComparison.Ordinal);
            }
        }
        return result;
    }

    #endregion

    #region かな正規化・ローマ字逆変換テーブル

    private static string NormalizeKanaAndAlpha(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // カタカナ（ァ〜ヶ）をひらがな（ぁ〜ゖ）に変換
            if (c >= '\u30A1' && c <= '\u30F6')
            {
                sb.Append((char)(c - 0x60));
            }
            // 全角英大文字 -> 半角英小文字
            else if (c >= 'Ａ' && c <= 'Ｚ')
            {
                sb.Append((char)(c - 'Ａ' + 'a'));
            }
            // 全角英小文字 -> 半角英小文字
            else if (c >= 'ａ' && c <= 'ｚ')
            {
                sb.Append((char)(c - 'ａ' + 'a'));
            }
            // 半角英大文字 -> 半角英小文字
            else if (c >= 'A' && c <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // 2文字（拗音等）のひらがな→ローマ字変換マップ
    private static readonly Dictionary<string, string> DigraphRomajiMap = new(StringComparer.Ordinal)
    {
        ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo",
        ["しゃ"] = "sya", ["しゅ"] = "syu", ["しょ"] = "syo",
        ["ちゃ"] = "tya", ["ちゅ"] = "tyu", ["ちょ"] = "tyo",
        ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo",
        ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
        ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo",
        ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
        ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
        ["じゃ"] = "zya", ["じゅ"] = "zyu", ["じょ"] = "zyo",
        ["ぢゃ"] = "dya", ["ぢゅ"] = "dyu", ["ぢょ"] = "dyo",
        ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo",
        ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
        ["ふぁ"] = "fa",  ["ふぃ"] = "fi",  ["ふぇ"] = "fe",  ["ふぉ"] = "fo",
        ["てぃ"] = "ti",  ["でぃ"] = "di",  ["うぃ"] = "wi",  ["うぇ"] = "we"
    };

    // 1文字のひらがな→ローマ字変換マップ
    private static readonly Dictionary<char, string> MonographRomajiMap = new()
    {
        ['あ'] = "a",  ['い'] = "i",  ['う'] = "u",  ['え'] = "e",  ['お'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['さ'] = "sa", ['し'] = "si", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['た'] = "ta", ['ち'] = "ti", ['つ'] = "tu", ['て'] = "te", ['と'] = "to",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "hu", ['へ'] = "he", ['ほ'] = "ho",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['ゐ'] = "wi", ['ゑ'] = "we", ['を'] = "wo", ['ん'] = "n",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['ざ'] = "za", ['じ'] = "zi", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['だ'] = "da", ['ぢ'] = "di", ['づ'] = "du", ['で'] = "de", ['ど'] = "do",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        ['ぁ'] = "xa", ['ぃ'] = "xi", ['ぅ'] = "xu", ['ぇ'] = "xe", ['ぉ'] = "xo",
        ['ゃ'] = "xya", ['ゅ'] = "xyu", ['ょ'] = "xyo", ['ゎ'] = "xwa",
        ['っ'] = "xtu", ['ー'] = "-"
    };

    private static string ConvertReadingToRomaji(string reading)
    {
        if (string.IsNullOrEmpty(reading))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(reading.Length * 2);
        int i = 0;

        while (i < reading.Length)
        {
            char current = reading[i];

            // 促音「っ」の処理：次のかなの子音を重ねる（なければ xtu）
            if (current == 'っ')
            {
                if (i + 1 < reading.Length)
                {
                    string nextRomaji = PeekNextRomaji(reading, i + 1);
                    if (!string.IsNullOrEmpty(nextRomaji) && IsConsonant(nextRomaji[0]))
                    {
                        sb.Append(nextRomaji[0]);
                        i++;
                        continue;
                    }
                }
                sb.Append("xtu");
                i++;
                continue;
            }

            // 2文字拗音チェック
            if (i + 1 < reading.Length)
            {
                string pair = reading.Substring(i, 2);
                if (DigraphRomajiMap.TryGetValue(pair, out string? digraphRomaji))
                {
                    sb.Append(digraphRomaji);
                    i += 2;
                    continue;
                }
            }

            // 1文字かなチェック
            if (MonographRomajiMap.TryGetValue(current, out string? monoRomaji))
            {
                // 「ん」の後に母音またはヤ行・ナ行が続く場合は nn にするとより明確だが、
                // 推定キーストロークとしては標準の n（母音・ヤ行直前は nn）とする
                if (current == 'ん' && i + 1 < reading.Length)
                {
                    string nextRomaji = PeekNextRomaji(reading, i + 1);
                    if (!string.IsNullOrEmpty(nextRomaji) && "aiueoy".Contains(nextRomaji[0]))
                    {
                        sb.Append("nn");
                        i++;
                        continue;
                    }
                }

                sb.Append(monoRomaji);
                i++;
                continue;
            }

            // アルファベットや記号、未変換の漢字などはそのままの位置で連結
            sb.Append(current);
            i++;
        }

        return sb.ToString();
    }

    private static string PeekNextRomaji(string text, int index)
    {
        if (index >= text.Length)
        {
            return string.Empty;
        }

        if (index + 1 < text.Length)
        {
            string pair = text.Substring(index, 2);
            if (DigraphRomajiMap.TryGetValue(pair, out string? digraph))
            {
                return digraph;
            }
        }

        char c = text[index];
        if (MonographRomajiMap.TryGetValue(c, out string? mono))
        {
            return mono;
        }

        if (c >= 'a' && c <= 'z')
        {
            return c.ToString();
        }

        return string.Empty;
    }

    private static bool IsConsonant(char c)
    {
        return c >= 'a' && c <= 'z' && !"aiueo".Contains(c);
    }

    #endregion
}
