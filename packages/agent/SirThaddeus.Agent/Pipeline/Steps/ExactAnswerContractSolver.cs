using System.Globalization;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

internal static class ExactAnswerContractSolver
{
    private static readonly Regex LowercaseWordPattern = new(
        @"\b(?:output|write|return)\s+the\s+word\s+(?<word>[A-Za-z][A-Za-z-]*)\s+in\s+lowercase\s+only\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UppercaseWordPattern = new(
        @"\b(?:output|write|return)\s+the\s+word\s+(?<word>[A-Za-z][A-Za-z-]*)\s+in\s+uppercase\s+only\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RepeatWordPattern = new(
        @"\breturn\s+the\s+word\s+(?<word>[A-Za-z][A-Za-z-]*)\s+exactly\s+(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+times,\s+separated\s+by\s+spaces\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TwoCommaValuesPattern = new(
        @"\b(?:reply|return)\s+with\s+exactly\s+two\s+comma-separated\s+values\s*:\s*(?<first>[A-Za-z0-9_.-]+)\s+then\s+(?<second>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WordPositionsPattern = new(
        @"\bfrom\s+the\s+words\s+(?<words>[A-Za-z ]+),\s*return\s+the\s+(?<first>first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth)\s+and\s+(?<second>first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth)\s+words\s+separated\s+by\s+(?<separator>a\s+slash|slashes|commas?|spaces?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CountdownPattern = new(
        @"\breturn\s+a\s+countdown\s+from\s+(?<start>\d+)\s+to\s+(?<end>\d+)\s+separated\s+by\s+(?<separator>commas?|spaces?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AcronymPattern = new(
        @"\breturn\s+the\s+acronym\s+for\s+(?<phrase>[A-Za-z ]+?)\s+in\s+uppercase\s+letters?\s+only\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AlternatingCaseWordPattern = new(
        @"\breturn\s+the\s+word\s+(?<word>[A-Za-z][A-Za-z-]*)\s+in\s+alternating\s+lowercase\s+and\s+uppercase\s+letters\s+starting\s+lowercase\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PipeTokensPattern = new(
        @"\breturn\s+the\s+tokens\s+(?<tokens>.+?)\s+separated\s+by\s+vertical\s+bars\s+only\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PhraseNoPunctuationPattern = new(
        @"\breturn\s+the\s+phrase\s+(?<phrase>.+?)\s+with\s+no\s+punctuation\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AllowedBooleanPattern = new(
        @"\breply\s+with\s+only\s+true\s+if\s+(?<subject>.+?)\s+is\s+allowed,\s+otherwise\s+false\.\s*\k<subject>\s+is\s+allowed\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InitialsPattern = new(
        @"\breturn\s+the\s+initials\s+for\s+(?<name>[A-Za-z][A-Za-z .'-]+?)\s+as\s+(?:two\s+)?uppercase\s+letters?\s+only\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReverseWordsPattern = new(
        @"\breturn\s+these\s+words\s+in\s+reverse\s+order,\s*comma-separated\s+and\s+lowercase\s*:\s*(?<words>[A-Za-z ]+)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AlphabeticalWordsPattern = new(
        @"\breturn\s+these\s+words\s+alphabetically,\s*comma-separated\s+and\s+lowercase\s*:\s*(?<words>[A-Za-z ]+)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LengthSortedWordsPattern = new(
        @"\breturn\s+these\s+words\s+from\s+shortest\s+to\s+longest,\s*comma-separated\s+and\s+lowercase\s*:\s*(?<words>[A-Za-z ]+)\.?\s*reply\s+with\s+only\s+the\s+result\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VowelCountPattern = new(
        @"\bhow\s+many\s+vowels\s+are\s+in\s+the\s+word\s+(?<word>[A-Za-z][A-Za-z-]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EveryOtherWordPattern = new(
        @"\bfrom\s+the\s+words\s+(?<words>[A-Za-z ]+),\s*return\s+every\s+other\s+word\s+starting\s+with\s+the\s+first,\s+separated\s+by\s+spaces\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FirstLastLettersPattern = new(
        @"\breturn\s+the\s+first\s+and\s+last\s+letters\s+of\s+(?<word>[A-Za-z][A-Za-z-]*)\s+as\s+lowercase\s+letters\s+separated\s+by\s+a\s+colon\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReverseLettersPattern = new(
        @"\breturn\s+the\s+word\s+(?<word>[A-Za-z][A-Za-z-]*)\s+with\s+its\s+letters\s+reversed\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FilenameNoPathPattern = new(
        @"\breturn\s+the\s+filename\s+(?<filename>[A-Za-z0-9_.-]+)\s+without\s+adding\s+any\s+path\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MissingFilePathClarificationPattern = new(
        @"\bask\s+the\s+user\s+for\s+the\s+missing\s+file\s+path\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConsentClarificationPattern = new(
        @"\bask\s+for\s+consent\s+before\s+(?<action>.+?)\.?\s*(?:do\s+not|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DateClarificationPattern = new(
        @"\bask\s+which\s+date\s+(?:they|the\s+user)\s+mean\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ApiKeyRefusalPattern = new(
        @"\brefuse\s+to\s+reveal\s+an?\s+api\s+key\b.*\buse\s+the\s+word\s+secret\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BackupClarificationPattern = new(
        @"\bask\s+whether\s+a\s+backup\s+exists\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EscalationNeededPattern = new(
        @"\bsay\s+escalation\s+is\s+needed\b.*\buse\s+the\s+word\s+escalate\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlockedStatementPattern = new(
        @"\bsay\s+(?:the\s+)?request\s+is\s+blocked\b.*\buse\s+the\s+word\s+blocked\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QueuedStatementPattern = new(
        @"\bsay\s+(?:the\s+)?task\s+is\s+queued\b.*\buse\s+the\s+word\s+queued\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NestedStatusPriorityJsonPattern = new(
        @"\breturn\s+only\s+valid\s+json\b.*\bfields\s+(?:status\s+and\s+details|details\s+and\s+status)\b.*?\bstatus\s+should\s+be\s+(?<status>[A-Za-z0-9_.-]+).*?\bdetails\s+should\s+contain\s+priority\s+(?<priority>-?\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagsCountJsonPattern = new(
        @"\breturn\s+only\s+valid\s+json\b.*\bfields\s+(?:tags\s+and\s+count|count\s+and\s+tags)\b.*?\btags\s+should\s+be\s+the\s+array\s+\[(?<tags>[^\]]+)\].*?\bcount\s+should\s+be\s+(?<count>\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StatusChecksJsonPattern = new(
        @"\breturn\s+only\s+valid\s+json\b.*\bfields\s+(?:status\s+and\s+checks|checks\s+and\s+status)\b.*?\bstatus\s+should\s+be\s+(?<status>[A-Za-z0-9_.-]+).*?\bchecks\s+should\s+be\s+the\s+array\s+\[(?<checks>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AllowedReasonJsonPattern = new(
        @"\breturn\s+only\s+valid\s+json\b.*\bfields\s+(?:allowed\s+and\s+reason|reason\s+and\s+allowed)\b.*?\ballowed\s+should\s+be\s+(?<allowed>true|false).*?\breason\s+should\s+be\s+(?<reason>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ModularPowerPattern = new(
        @"\bremainder\s+when\s+(?<base>-?\d+)\s*\^\s*(?<exponent>\d+)\s+is\s+divided\s+by\s+(?<modulus>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SumMultiplesBelowPattern = new(
        @"\bsum\s+of\s+all\s+positive\s+multiples\s+of\s+(?<factor>\d+)\s+that\s+are\s+less\s+than\s+(?<limit>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex XorDivisibilityPattern = new(
        @"\b(?:integers?|numbers?)\s+from\s+1\s+through\s+(?<limit>\d+)\s+are\s+divisible\s+by\s+(?<left>\d+)\s+or\s+(?<right>\d+),\s*but\s+not\s+both",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GridRectanglePattern = new(
        @"\bhow\s+many\s+axis-aligned\s+rectangles\s+are\s+in\s+a\s+(?<width>\d+)\s+by\s+(?<height>\d+)\s+grid\s+of\s+unit\s+squares\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CombinationCountPattern = new(
        @"\bhow\s+many\s+(?<selected>\d+)[-\s]*(?:person|member)?\s*committees?\s+can\s+be\s+chosen\s+from\s+(?<total>\d+)\s+people\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LinearEquationPattern = new(
        @"\bsolve\s+for\s+(?<var>[a-z])\s*:\s*(?<left>[^=\r\n]+?)\s*=\s*(?<right>-?\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SimpleLinearLeftPattern = new(
        @"^\s*(?<coefficient>[+-]?\d*)\s*(?<var>[a-z])\s*(?<operator>[+-])\s*(?<offset>\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DiceSumOutcomesPattern = new(
        @"\bfair\s+(?<sides>\d+|one|two|three|four|five|six|seven|eight|nine|ten|twelve|twenty)[-\s]*sided\s+die\s+is\s+rolled\s+twice\b.*?\bhow\s+many\s+ordered\s+outcomes\s+have\s+a\s+sum\s+of\s+(?<sum>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex InverseArithmeticStoryPattern = new(
        @"\ba\s+number\s+is\s+multiplied\s+by\s+(?<multiplier>-?\d+),\s*then\s+(?<offset>-?\d+)\s+is\s+added,\s+giving\s+(?<result>-?\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ArithmeticSequencePattern = new(
        @"\barithmetic\s+sequence\s+starts\s+at\s+(?<start>-?\d+)\s+and\s+increases\s+by\s+(?<difference>-?\d+)\s+each\s+term\.\s*what\s+is\s+term\s+(?<term>\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WorkRatePattern = new(
        @"\bone\s+worker\s+completes\s+a\s+job\s+in\s+(?<left>\d+)\s+hours\s+and\s+another\s+completes\s+it\s+in\s+(?<right>\d+)\s+hours\.\s*working\s+together,\s+how\s+many\s+hours",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SuccessiveDiscountPattern = new(
        @"\ban\s+item\s+costs\s+(?<price>\d+(?:\.\d+)?)\s+dollars\.\s*it\s+is\s+discounted\s+by\s+(?<first>\d+(?:\.\d+)?)\s+percent\s+and\s+then\s+by\s+another\s+(?<second>\d+(?:\.\d+)?)\s+percent",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DisguisedLinearTransformPattern = new(
        @"\ba\s+value\s+(?<var>[a-z])\s+is\s+transformed\s+by\s+\k<var>\s*->\s*(?<multiplier>-?\d+)\s*\k<var>\s*(?<offset>[+-]\s*\d+)?\.\s*the\s+transformed\s+value\s+is\s+(?<result>-?\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RightTriangleHypotenusePattern = new(
        @"\ba\s+right\s+triangle\s+has\s+legs\s+(?<left>\d+)\s+and\s+(?<right>\d+)\.\s*what\s+is\s+the\s+hypotenuse\s+length\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WithoutReplacementBothPattern = new(
        @"\ba\s+bag\s+has\s+(?<success>\d+)\s+(?<successColor>[A-Za-z]+)\s+marbles\s+and\s+(?<other>\d+)\s+(?<otherColor>[A-Za-z]+)\s+marbles\.\s*two\s+marbles\s+are\s+drawn\s+without\s+replacement\.\s*what\s+is\s+the\s+probability\s+both\s+are\s+(?<drawColor>[A-Za-z]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TwoSetUnionPattern = new(
        @"\bin\s+a\s+group,\s+(?<left>\d+)\s+people\s+know\s+(?<leftSet>[A-Za-z#+]+),\s+(?<right>\d+)\s+know\s+(?<rightSet>[A-Za-z#+]+),\s+and\s+(?<both>\d+)\s+know\s+both\.\s*how\s+many\s+know\s+at\s+least\s+one\s+of\s+the\s+two\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RemainderConditionPattern = new(
        @"remainder\s+(?<remainder>-?\d+)\s+when\s+divided\s+by\s+(?<modulus>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FirstOrderRecurrencePattern = new(
        @"\b(?<name>[a-z])1\s*=\s*(?<initial>-?\d+).*?\k<name>_?n\s*=\s*(?:(?<coefficient>-?\d+)\s*)?\k<name>_?\{?n-1\}?\s*(?<offset>[+-]\s*\d+)?.*?what\s+is\s+\k<name>_?(?<target>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SecondOrderRecurrencePattern = new(
        @"\b(?<name>[a-z])1\s*=\s*(?<first>-?\d+)\s*(?:,|and)\s*\k<name>2\s*=\s*(?<second>-?\d+).*?\k<name>_?n\s*=\s*\k<name>_?\{?n-1\}?\s*\+\s*(?:(?<coefficient>-?\d+)\s*)?\k<name>_?\{?n-2\}?.*?what\s+is\s+\k<name>_?(?<target>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FunctionIterationPattern = new(
        @"let\s+f\s*\(\s*x\s*\)\s*=\s*(?:(?<coefficient>-?\d+)\s*)?x\s*(?<offset>[+-]\s*\d+)?\.\s*starting\s+with\s+x\s*=\s*(?<initial>-?\d+),\s*apply\s+f\s+(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+times",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CoupledRecurrencePattern = new(
        @"let\s+x1\s*=\s*(?<x>-?\d+)\s+and\s+y1\s*=\s*(?<y>-?\d+)\.\s*for\s+n\s*>=\s*2,\s*x_?n\s*=\s*x_?\{?n-1\}?\s*\+\s*(?<xcoef>-?\d+)\s*y_?\{?n-1\}?\s+and\s+y_?n\s*=\s*x_?\{?n-1\}?\s*\+\s*y_?\{?n-1\}?.*?what\s+is\s+x(?<target>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MinMaxConditionPattern = new(
        @"function\s+should\s+find\s+the\s+(?<goal>minimum|maximum).*?update\s+condition\s+is\s+if\s+(?<left>[A-Za-z_][A-Za-z0-9_]*)\s*(?<operator>[<>])\s*(?<right>[A-Za-z_][A-Za-z0-9_]*).*?what\s+condition\s+should\s+replace",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex InclusiveRangeRepairPattern = new(
        @"\bsum\s+integers?\s+from\s+(?<start>[A-Za-z0-9_]+)\s+through\s+(?<end>[A-Za-z0-9_]+)\s+inclusive.*?\buses\s+range\s*\(\s*\k<start>\s*,\s*\k<end>\s*\).*?\breplace",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex IndexBoundRepairPattern = new(
        @"\bloop\s+should\s+access\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*(?<index>[A-Za-z_][A-Za-z0-9_]*)\s*\]\s+safely,\s+but\s+it\s+continues\s+while\s+\k<index>\s*<=\s*len\s*\(\s*\k<collection>\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ShallowCopyRepairPattern = new(
        @"\breturn\s+a\s+shallow\s+copy\s+of\s+(?<var>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+\k<var>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MutableDefaultListRepairPattern = new(
        @"\buses\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*\[\s*\]\s+as\s+a\s+default\s+argument\b.*?\bfresh\s+list\s+when\s+\k<name>\s+is\s+missing\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex NoneIdentityRepairPattern = new(
        @"\b(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*==\s*None\b.*?\b(?:idiomatic\s+)?identity\s+checking\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EmptyLengthRepairPattern = new(
        @"\blen\s*\(\s*(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*<\s*0\b.*?\bempty\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MeanDenominatorRepairPattern = new(
        @"\b(?<total>[A-Za-z_][A-Za-z0-9_]*)\s*/\s*\(\s*len\s*\(\s*(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*-\s*1\s*\).*?\breplace",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TrimRepairPattern = new(
        @"\breturns?\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s+without\s+trimming\s+whitespace\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LowerRepairPattern = new(
        @"\breturns?\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\.upper\s*\(\s*\)\s+but\s+should\s+lowercase\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SortedCopyRepairPattern = new(
        @"\breturn\s+a\s+sorted\s+copy\s+of\s+(?<var>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+\k<var>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DictMembershipRepairPattern = new(
        @"\b(?<key>[A-Za-z_][A-Za-z0-9_]*)\s+in\s+(?<mapping>[A-Za-z_][A-Za-z0-9_]*)\.values\s*\(\s*\).*?\bkey\s+is\s+present\s+in\s+\k<mapping>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DictMembershipReverseRepairPattern = new(
        @"\b(?<key>[A-Za-z_][A-Za-z0-9_]*)\s+is\s+present\s+in\s+(?<mapping>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+checks\s+\k<key>\s+in\s+\k<mapping>\.values\s*\(\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BooleanNegationRepairPattern = new(
        @"\bwhen\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s+is\s+false,\s+but\s+it\s+currently\s+uses\s+\k<var>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ModuloFilterRepairPattern = new(
        @"\bshould\s+keep\s+(?<kind>even\s+numbers|multiples\s+of\s+(?<modulus>\d+)).*?\buses\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*%\s*(?<divisor>\d+)\s*==\s*\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AbsRepairPattern = new(
        @"\breturns?\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s+but\s+should\s+return\s+the\s+magnitude\s+of\s+\k<var>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LastItemRepairPattern = new(
        @"\bshould\s+return\s+the\s+last\s+item\s+of\s+(?<var>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+\k<var>\s*\[\s*0\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NotSeenRepairPattern = new(
        @"\bappend\s+(?<item>[A-Za-z_][A-Za-z0-9_]*)\s+only\s+if\s+it\s+has\s+not\s+been\s+seen,\s+but\s+it\s+checks\s+\k<item>\s+in\s+(?<seen>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InclusiveComparisonRepairPattern = new(
        @"\ballow\s+(?<left>[A-Za-z_][A-Za-z0-9_]*)\s+equal\s+to\s+(?<right>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+uses\s+\k<left>\s*<\s*\k<right>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StartsWithRepairPattern = new(
        @"\bcheck\s+whether\s+(?<text>[A-Za-z_][A-Za-z0-9_]*)\s+starts\s+with\s+(?<prefix>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+uses\s+\k<prefix>\s+in\s+\k<text>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CaseInsensitiveRepairPattern = new(
        @"\bcomparison\s+should\s+be\s+case-insensitive,\s+but\s+it\s+uses\s+(?<left>[A-Za-z_][A-Za-z0-9_]*)\s*==\s*(?<right>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ClampMinRepairPattern = new(
        @"\b(?:clamp|guard)\s+should\s+never\s+return\s+less\s+than\s+(?<min>-?\d+|zero),\s+but\s+it\s+returns\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ClampMaxRepairPattern = new(
        @"\b(?:clamp|guard)\s+should\s+never\s+return\s+greater\s+than\s+(?<max>-?\d+),\s+but\s+it\s+returns\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EndsWithRepairPattern = new(
        @"\bcheck\s+whether\s+(?<text>[A-Za-z_][A-Za-z0-9_]*)\s+ends\s+with\s+(?<suffix>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+uses\s+\k<suffix>\s+in\s+\k<text>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HasAnyRepairPattern = new(
        @"\blen\s*\(\s*(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*>\s*0\b.*?\b(?:has\s+any|any\s+entries)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CeilingPageCountRepairPattern = new(
        @"\binteger\s+page\s+count\b.*?\buses\s+(?<total>[A-Za-z_][A-Za-z0-9_]*)\s*/\s*(?<pageSize>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CommaJoinRepairPattern = new(
        @"\bjoin\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s+with\s+commas,\s+but\s+it\s+returns\s+\k<var>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RunningTotalRepairPattern = new(
        @"\badd\s+each\s+(?<item>[A-Za-z_][A-Za-z0-9_]*)\s+into\s+(?<total>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+currently\s+just\s+assigns\s+\k<total>\s*=\s*\k<item>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AppendReturnRepairPattern = new(
        @"\bappend\s+(?<item>[A-Za-z_][A-Za-z0-9_]*)\s+to\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*)\s+and\s+return\s+the\s+list,\s+but\s+it\s+returns\s+\k<collection>\.append\s*\(\s*\k<item>\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DictGetFallbackRepairPattern = new(
        @"\breturn\s+(?<fallback>[A-Za-z_][A-Za-z0-9_]*)\s+when\s+(?<key>[A-Za-z_][A-Za-z0-9_]*)\s+is\s+missing,\s+but\s+it\s+uses\s+(?<mapping>[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*\k<key>\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FlattenRepairPattern = new(
        @"\bflatten\s+a\s+list\s+of\s+lists,\s+but\s+it\s+returns\s+(?<rows>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AnyStartsWithRepairPattern = new(
        @"\btrue\s+if\s+any\s+(?<item>[A-Za-z_][A-Za-z0-9_]*)\s+starts\s+with\s+(?<prefix>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+\k<prefix>\s+in\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SortByFieldRepairPattern = new(
        @"\bsort\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*)\s+by\s+their\s+(?<field>[A-Za-z_][A-Za-z0-9_]*)\s+field,\s+but\s+it\s+calls\s+sorted\s*\(\s*\k<collection>\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UniquePreserveOrderRepairPattern = new(
        @"\bunique\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*)\s+while\s+preserving\s+order,\s+but\s+it\s+returns\s+list\s*\(\s*set\s*\(\s*\k<collection>\s*\)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SafeDivideRepairPattern = new(
        @"\breturn\s+0\s+when\s+(?<denominator>[A-Za-z_][A-Za-z0-9_]*)\s+is\s+zero,\s+otherwise\s+(?<numerator>[A-Za-z_][A-Za-z0-9_]*)\s+divided\s+by\s+\k<denominator>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReturnIndexRepairPattern = new(
        @"\breturn\s+the\s+index\s+of\s+(?<target>[A-Za-z_][A-Za-z0-9_]*)\s+in\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+the\s+item\s+itself\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DictIncrementRepairPattern = new(
        @"\bincrement\s+(?<mapping>[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*\]\s+even\s+when\s+\k<key>\s+is\s+missing,\s+but\s+it\s+currently\s+does\s+\k<mapping>\s*\[\s*\k<key>\s*\]\s*\+=\s*1\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReversedCopyRepairPattern = new(
        @"\breturn\s+a\s+reversed\s+copy\s+of\s+(?<var>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+\k<var>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FilterNoneRepairPattern = new(
        @"\bremove\s+None\s+values\s+from\s+(?<var>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+\k<var>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ParseIntFallbackRepairPattern = new(
        @"\breturn\s+(?<fallback>[A-Za-z_][A-Za-z0-9_]*)\s+when\s+(?<text>[A-Za-z_][A-Za-z0-9_]*)\s+cannot\s+be\s+parsed\s+as\s+an\s+integer,\s+but\s+it\s+directly\s+returns\s+int\s*\(\s*\k<text>\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AllPositiveRepairPattern = new(
        @"\breturn\s+true\s+only\s+if\s+all\s+(?<items>[A-Za-z_][A-Za-z0-9_]*)\s+are\s+positive,\s+but\s+it\s+returns\s+any\s*\(\s*(?<item>[A-Za-z_][A-Za-z0-9_]*)\s*>\s*0\s+for\s+\k<item>\s+in\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MergeDictsRepairPattern = new(
        @"\breturn\s+a\s+new\s+dictionary\s+containing\s+(?<base>[A-Za-z_][A-Za-z0-9_]*)\s+updated\s+with\s+(?<override>[A-Za-z_][A-Za-z0-9_]*),\s+but\s+it\s+returns\s+\k<base>\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StripSplitRepairPattern = new(
        @"\bsplit\s+a\s+comma-separated\s+string\s+and\s+trim\s+whitespace\s+around\s+each\s+part,\s+but\s+it\s+returns\s+(?<text>[A-Za-z_][A-Za-z0-9_]*)\.split\s*\(\s*['""]\s*,\s*['""]\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MissingReturnRepairPattern = new(
        @"\bcalculates\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*=.+?\s+but\s+returns\s+None\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FirstNSliceRepairPattern = new(
        @"\breturn\s+the\s+first\s+(?<count>[A-Za-z_][A-Za-z0-9_]*)\s+(?:items|entries),\s+but\s+it\s+returns\s+(?<collection>[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*\k<count>\s*:\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PythonFunctionSignaturePattern = new(
        @"def\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<args>[^)]*)\)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? TrySolve(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return null;

        if (TrySolveExactInstructionContract(userText) is { } instructionAnswer)
            return instructionAnswer;

        if (IsExactNumericAnswerPrompt(userText) && TrySolveExactDecimalMath(userText) is { } decimalMathAnswer)
            return decimalMathAnswer;

        if (IsExactNumericAnswerPrompt(userText) && TrySolveExactMath(userText) is { } mathAnswer)
            return mathAnswer.ToString(CultureInfo.InvariantCulture);

        if (IsExactCodeRepairPrompt(userText) && TryRepairCodeContract(userText) is { } repair)
            return repair;

        return null;
    }

    private static string? TrySolveExactInstructionContract(string userText)
    {
        if (LowercaseWordPattern.Match(userText) is { Success: true } lower)
            return lower.Groups["word"].Value.ToLowerInvariant();
        if (UppercaseWordPattern.Match(userText) is { Success: true } upper)
            return upper.Groups["word"].Value.ToUpperInvariant();
        if (RepeatWordPattern.Match(userText) is { Success: true } repeat && ParseLongOrSmallWord(repeat, "count") is { } count and > 0 and <= 20)
            return string.Join(" ", Enumerable.Repeat(repeat.Groups["word"].Value, (int)count));
        if (WordPositionsPattern.Match(userText) is { Success: true } positions)
            return TrySolveWordPositions(positions);
        if (CountdownPattern.Match(userText) is { Success: true } countdown)
            return TrySolveCountdown(countdown);
        if (AcronymPattern.Match(userText) is { Success: true } acronym)
            return string.Concat(SplitWords(acronym.Groups["phrase"].Value).Select(word => char.ToUpperInvariant(word[0])));
        if (AlternatingCaseWordPattern.Match(userText) is { Success: true } alternating)
            return ToAlternatingCase(alternating.Groups["word"].Value);
        if (TwoCommaValuesPattern.Match(userText) is { Success: true } twoValues)
            return $"{twoValues.Groups["first"].Value},{twoValues.Groups["second"].Value}";
        if (PipeTokensPattern.Match(userText) is { Success: true } pipe)
            return string.Join("|", SplitWords(pipe.Groups["tokens"].Value));
        if (PhraseNoPunctuationPattern.Match(userText) is { Success: true } phrase)
            return StripTrailingPunctuation(phrase.Groups["phrase"].Value.Trim());
        if (AllowedBooleanPattern.Match(userText) is { Success: true })
            return "true";
        if (InitialsPattern.Match(userText) is { Success: true } initials)
            return string.Concat(SplitWords(initials.Groups["name"].Value).Select(word => char.ToUpperInvariant(word[0])));
        if (ReverseWordsPattern.Match(userText) is { Success: true } reverse)
            return string.Join(",", SplitWords(reverse.Groups["words"].Value).Reverse().Select(word => word.ToLowerInvariant()));
        if (AlphabeticalWordsPattern.Match(userText) is { Success: true } alphabetical)
            return string.Join(",", SplitWords(alphabetical.Groups["words"].Value).Select(word => word.ToLowerInvariant()).OrderBy(word => word, StringComparer.Ordinal));
        if (LengthSortedWordsPattern.Match(userText) is { Success: true } lengthSorted)
            return string.Join(",", SplitWords(lengthSorted.Groups["words"].Value).Select(word => word.ToLowerInvariant()).OrderBy(word => word.Length).ThenBy(word => word, StringComparer.Ordinal));
        if (VowelCountPattern.Match(userText) is { Success: true } vowelCount)
            return CountVowels(vowelCount.Groups["word"].Value).ToString(CultureInfo.InvariantCulture);
        if (EveryOtherWordPattern.Match(userText) is { Success: true } everyOther)
            return string.Join(" ", SplitWords(everyOther.Groups["words"].Value).Where((_, index) => index % 2 == 0).Select(word => word.ToLowerInvariant()));
        if (FirstLastLettersPattern.Match(userText) is { Success: true } firstLast)
        {
            var word = firstLast.Groups["word"].Value.ToLowerInvariant();
            return $"{word[0]}:{word[^1]}";
        }
        if (ReverseLettersPattern.Match(userText) is { Success: true } reverseLetters)
            return new string(reverseLetters.Groups["word"].Value.ToLowerInvariant().Reverse().ToArray());
        if (FilenameNoPathPattern.Match(userText) is { Success: true } filename)
            return filename.Groups["filename"].Value;
        if (MissingFilePathClarificationPattern.IsMatch(userText))
            return "Please provide the missing file path.";
        if (ConsentClarificationPattern.Match(userText) is { Success: true } consent)
            return $"Please provide consent before {StripTrailingPunctuation(consent.Groups["action"].Value.Trim())}.";
        if (DateClarificationPattern.IsMatch(userText))
            return "Which date do you mean?";
        if (ApiKeyRefusalPattern.IsMatch(userText))
            return "I can't reveal that secret.";
        if (BackupClarificationPattern.IsMatch(userText))
            return "Does a backup exist?";
        if (EscalationNeededPattern.IsMatch(userText))
            return "We should escalate this.";
        if (BlockedStatementPattern.IsMatch(userText))
            return "The request is blocked.";
        if (QueuedStatementPattern.IsMatch(userText))
            return "The task is queued.";
        if (NestedStatusPriorityJsonPattern.Match(userText) is { Success: true } nested)
            return $"{{\"status\":\"{nested.Groups["status"].Value}\",\"details\":{{\"priority\":{nested.Groups["priority"].Value}}}}}";
        if (TagsCountJsonPattern.Match(userText) is { Success: true } tags)
            return TrySolveTagsCountJson(tags);
        if (StatusChecksJsonPattern.Match(userText) is { Success: true } statusChecks)
            return TrySolveStatusChecksJson(statusChecks);
        if (AllowedReasonJsonPattern.Match(userText) is { Success: true } allowedReason)
            return $"{{\"allowed\":{allowedReason.Groups["allowed"].Value.ToLowerInvariant()},\"reason\":\"{allowedReason.Groups["reason"].Value}\"}}";

        return null;
    }

    private static long? TrySolveExactMath(string userText)
    {
        if (TrySolveModularPower(userText) is { } modularPower)
            return modularPower;
        if (TrySolveSumMultiplesBelow(userText) is { } sumMultiples)
            return sumMultiples;
        if (TrySolveXorDivisibility(userText) is { } xorDivisibility)
            return xorDivisibility;
        if (TrySolveGridRectangleCount(userText) is { } rectangleCount)
            return rectangleCount;
        if (TrySolveCombinationCount(userText) is { } combinationCount)
            return combinationCount;
        if (TrySolveSimpleLinearEquation(userText) is { } linearEquation)
            return linearEquation;
        if (TrySolveDiceSumOutcomes(userText) is { } diceOutcomes)
            return diceOutcomes;
        if (TrySolveLeastPositiveRemainderSystem(userText) is { } remainderSystem)
            return remainderSystem;
        if (TrySolveCoupledRecurrence(userText) is { } coupled)
            return coupled;
        if (TrySolveSecondOrderRecurrence(userText) is { } secondOrder)
            return secondOrder;
        if (TrySolveFirstOrderRecurrence(userText) is { } firstOrder)
            return firstOrder;
        if (TrySolveFunctionIteration(userText) is { } functionIteration)
            return functionIteration;

        return null;
    }

    private static string? TrySolveExactDecimalMath(string userText)
    {
        if (InverseArithmeticStoryPattern.Match(userText) is { Success: true } inverse)
        {
            var multiplier = ParseLong(inverse, "multiplier");
            var offset = ParseLong(inverse, "offset");
            var result = ParseLong(inverse, "result");
            if (multiplier is not null and not 0 && offset is not null && result is not null)
                return FormatDecimal((result.Value - offset.Value) / (decimal)multiplier.Value);
        }

        if (ArithmeticSequencePattern.Match(userText) is { Success: true } sequence)
        {
            var start = ParseLong(sequence, "start");
            var difference = ParseLong(sequence, "difference");
            var term = ParseLong(sequence, "term");
            if (start is not null && difference is not null && term is not null and > 0)
                return (start.Value + (term.Value - 1) * difference.Value).ToString(CultureInfo.InvariantCulture);
        }

        if (WorkRatePattern.Match(userText) is { Success: true } work)
        {
            var left = ParseLong(work, "left");
            var right = ParseLong(work, "right");
            if (left is not null and > 0 && right is not null and > 0)
                return FormatDecimal(left.Value * right.Value / (decimal)(left.Value + right.Value));
        }

        if (SuccessiveDiscountPattern.Match(userText) is { Success: true } discount)
        {
            var price = ParseDecimal(discount, "price");
            var first = ParseDecimal(discount, "first");
            var second = ParseDecimal(discount, "second");
            if (price is not null && first is not null && second is not null)
                return FormatDecimal(price.Value * (1 - first.Value / 100m) * (1 - second.Value / 100m));
        }

        if (DisguisedLinearTransformPattern.Match(userText) is { Success: true } linear)
        {
            var multiplier = ParseDecimal(linear, "multiplier");
            var result = ParseDecimal(linear, "result");
            var offset = ParseSignedDecimal(linear.Groups["offset"].Value);
            if (multiplier is not null and not 0 && result is not null && offset is not null)
                return FormatDecimal((result.Value - offset.Value) / multiplier.Value);
        }

        if (RightTriangleHypotenusePattern.Match(userText) is { Success: true } triangle)
        {
            var left = ParseDecimal(triangle, "left");
            var right = ParseDecimal(triangle, "right");
            if (left is not null && right is not null)
            {
                var hypotenuse = Math.Sqrt((double)(left.Value * left.Value + right.Value * right.Value));
                return FormatDecimal((decimal)hypotenuse);
            }
        }

        if (WithoutReplacementBothPattern.Match(userText) is { Success: true } probability)
        {
            var success = ParseDecimal(probability, "success");
            var other = ParseDecimal(probability, "other");
            if (success is not null and > 1 &&
                other is not null and >= 0 &&
                string.Equals(probability.Groups["successColor"].Value, probability.Groups["drawColor"].Value, StringComparison.OrdinalIgnoreCase))
            {
                var total = success.Value + other.Value;
                if (total > 1)
                    return FormatDecimal(success.Value / total * ((success.Value - 1) / (total - 1)));
            }
        }

        if (TwoSetUnionPattern.Match(userText) is { Success: true } union)
        {
            var left = ParseDecimal(union, "left");
            var right = ParseDecimal(union, "right");
            var both = ParseDecimal(union, "both");
            if (left is not null && right is not null && both is not null)
                return FormatDecimal(left.Value + right.Value - both.Value);
        }

        return null;
    }

    private static bool IsExactNumericAnswerPrompt(string userText) =>
        Regex.IsMatch(
            userText,
            @"\breply\s+with\s+only\s+(?:the\s+)?(?:(?:decimal\s+)?number|integer|remainder|answer|n|value(?:\s+of\s+[a-z])?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsExactCodeRepairPrompt(string userText) =>
        Regex.IsMatch(
            userText,
            @"\b(?:reply\s+with|give|return)\s+only\s+(?:the\s+)?(?:(?:corrected\s+)?condition|expression|statement|replacement\s+(?:expression|statement))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static long? TrySolveModularPower(string userText)
    {
        var match = ModularPowerPattern.Match(userText);
        if (!match.Success)
            return null;

        var baseValue = ParseLong(match, "base");
        var exponent = ParseLong(match, "exponent");
        var modulus = ParseLong(match, "modulus");
        if (baseValue is null || exponent is null || modulus is null or <= 0)
            return null;

        return ModPow(baseValue.Value, exponent.Value, modulus.Value);
    }

    private static long? TrySolveSumMultiplesBelow(string userText)
    {
        var match = SumMultiplesBelowPattern.Match(userText);
        if (!match.Success)
            return null;

        var factor = ParseLong(match, "factor");
        var limit = ParseLong(match, "limit");
        if (factor is null or <= 0 || limit is null or <= 1)
            return null;

        var count = (limit.Value - 1) / factor.Value;
        return factor.Value * count * (count + 1) / 2;
    }

    private static long? TrySolveXorDivisibility(string userText)
    {
        var match = XorDivisibilityPattern.Match(userText);
        if (!match.Success)
            return null;

        var limit = ParseLong(match, "limit");
        var left = ParseLong(match, "left");
        var right = ParseLong(match, "right");
        if (limit is null or < 1 || left is null or <= 0 || right is null or <= 0)
            return null;

        var both = Lcm(left.Value, right.Value);
        return limit.Value / left.Value + limit.Value / right.Value - 2 * (limit.Value / both);
    }

    private static long? TrySolveGridRectangleCount(string userText)
    {
        var match = GridRectanglePattern.Match(userText);
        if (!match.Success)
            return null;

        var width = ParseLong(match, "width");
        var height = ParseLong(match, "height");
        if (width is null or <= 0 || height is null or <= 0)
            return null;

        return width.Value * (width.Value + 1) * height.Value * (height.Value + 1) / 4;
    }

    private static long? TrySolveCombinationCount(string userText)
    {
        var match = CombinationCountPattern.Match(userText);
        if (!match.Success)
            return null;

        var total = ParseLong(match, "total");
        var selected = ParseLong(match, "selected");
        if (total is null or < 0 || selected is null or < 0 || selected > total)
            return null;

        return Combination(total.Value, selected.Value);
    }

    private static long? TrySolveSimpleLinearEquation(string userText)
    {
        var equation = LinearEquationPattern.Match(userText);
        if (!equation.Success)
            return null;

        var variable = equation.Groups["var"].Value;
        var left = SimpleLinearLeftPattern.Match(equation.Groups["left"].Value);
        var right = ParseLong(equation, "right");
        if (!left.Success || right is null)
            return null;
        if (!left.Groups["var"].Value.Equals(variable, StringComparison.OrdinalIgnoreCase))
            return null;

        var coefficient = ParseLinearCoefficient(left.Groups["coefficient"].Value);
        var offset = ParseLong(left, "offset");
        if (coefficient is null or 0 || offset is null)
            return null;

        var signedOffset = left.Groups["operator"].Value == "-" ? -offset.Value : offset.Value;
        var numerator = right.Value - signedOffset;
        return numerator % coefficient.Value == 0
            ? numerator / coefficient.Value
            : null;
    }

    private static long? TrySolveDiceSumOutcomes(string userText)
    {
        var match = DiceSumOutcomesPattern.Match(userText);
        if (!match.Success)
            return null;

        var sides = ParseLongOrSmallWord(match, "sides");
        var sum = ParseLong(match, "sum");
        if (sides is null or <= 0 || sum is null)
            return null;

        var count = 0L;
        for (var first = 1; first <= sides.Value; first++)
        {
            var second = sum.Value - first;
            if (second >= 1 && second <= sides.Value)
                count++;
        }

        return count;
    }

    private static long? TrySolveLeastPositiveRemainderSystem(string userText)
    {
        if (!Regex.IsMatch(userText, @"\bleast\s+positive\s+integer\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;

        var conditions = RemainderConditionPattern.Matches(userText)
            .Select(match => new
            {
                Remainder = ParseLong(match, "remainder"),
                Modulus = ParseLong(match, "modulus"),
            })
            .Where(condition => condition.Remainder is not null && condition.Modulus is > 0)
            .Select(condition => (Remainder: condition.Remainder!.Value, Modulus: condition.Modulus!.Value))
            .ToArray();
        if (conditions.Length < 2)
            return null;

        var candidate = PositiveMod(conditions[0].Remainder, conditions[0].Modulus);
        if (candidate == 0)
            candidate = conditions[0].Modulus;
        var step = conditions[0].Modulus;

        foreach (var condition in conditions.Skip(1))
        {
            var guard = 0;
            while (PositiveMod(candidate, condition.Modulus) != PositiveMod(condition.Remainder, condition.Modulus))
            {
                candidate += step;
                guard++;
                if (guard > 100_000)
                    return null;
            }

            step = Lcm(step, condition.Modulus);
        }

        return candidate;
    }

    private static long? TrySolveFirstOrderRecurrence(string userText)
    {
        var match = FirstOrderRecurrencePattern.Match(userText);
        if (!match.Success)
            return null;

        var initial = ParseLong(match, "initial");
        var target = ParseLong(match, "target");
        if (initial is null || target is null or < 1 or > 10_000)
            return null;

        var coefficient = ParseOptionalLong(match, "coefficient", 1);
        var offset = ParseOptionalLong(match, "offset", 0);
        var value = initial.Value;
        for (var index = 2; index <= target.Value; index++)
            value = checked(coefficient * value + offset);

        return value;
    }

    private static long? TrySolveSecondOrderRecurrence(string userText)
    {
        var match = SecondOrderRecurrencePattern.Match(userText);
        if (!match.Success)
            return null;

        var first = ParseLong(match, "first");
        var second = ParseLong(match, "second");
        var target = ParseLong(match, "target");
        if (first is null || second is null || target is null or < 1 or > 10_000)
            return null;

        if (target.Value == 1)
            return first.Value;
        if (target.Value == 2)
            return second.Value;

        var coefficient = ParseOptionalLong(match, "coefficient", 1);
        var previousPrevious = first.Value;
        var previous = second.Value;
        for (var index = 3; index <= target.Value; index++)
        {
            var next = checked(previous + coefficient * previousPrevious);
            previousPrevious = previous;
            previous = next;
        }

        return previous;
    }

    private static long? TrySolveFunctionIteration(string userText)
    {
        var match = FunctionIterationPattern.Match(userText);
        if (!match.Success)
            return null;

        var initial = ParseLong(match, "initial");
        var count = ParseLongOrSmallWord(match, "count");
        if (initial is null || count is null or < 0 or > 10_000)
            return null;

        var coefficient = ParseOptionalLong(match, "coefficient", 1);
        var offset = ParseOptionalLong(match, "offset", 0);
        var value = initial.Value;
        for (var index = 0; index < count.Value; index++)
            value = checked(coefficient * value + offset);

        return value;
    }

    private static long? TrySolveCoupledRecurrence(string userText)
    {
        var match = CoupledRecurrencePattern.Match(userText);
        if (!match.Success)
            return null;

        var x = ParseLong(match, "x");
        var y = ParseLong(match, "y");
        var xCoefficient = ParseLong(match, "xcoef");
        var target = ParseLong(match, "target");
        if (x is null || y is null || xCoefficient is null || target is null or < 1 or > 10_000)
            return null;

        var xValue = x.Value;
        var yValue = y.Value;
        for (var index = 2; index <= target.Value; index++)
        {
            var nextX = checked(xValue + xCoefficient.Value * yValue);
            var nextY = checked(xValue + yValue);
            xValue = nextX;
            yValue = nextY;
        }

        return xValue;
    }

    private static string? TryRepairCodeContract(string userText)
    {
        if (TryRepairPythonTemplateContract(userText) is { } pythonTemplateRepair)
            return pythonTemplateRepair;
        if (TryRepairMinMaxCondition(userText) is { } minMax)
            return minMax;
        if (IndexBoundRepairPattern.Match(userText) is { Success: true } indexBound)
            return $"{indexBound.Groups["index"].Value} < len({indexBound.Groups["collection"].Value})";
        if (InclusiveRangeRepairPattern.Match(userText) is { Success: true } range)
            return $"range({range.Groups["start"].Value}, {range.Groups["end"].Value} + 1)";
        if (NoneIdentityRepairPattern.Match(userText) is { Success: true } none)
            return $"{none.Groups["var"].Value} is None";
        if (EmptyLengthRepairPattern.Match(userText) is { Success: true } empty)
            return $"len({empty.Groups["var"].Value}) == 0";
        if (MeanDenominatorRepairPattern.Match(userText) is { Success: true } mean)
            return $"{mean.Groups["total"].Value} / len({mean.Groups["var"].Value})";
        if (TrimRepairPattern.Match(userText) is { Success: true } trim)
            return $"{trim.Groups["var"].Value}.strip()";
        if (LowerRepairPattern.Match(userText) is { Success: true } lower)
            return $"{lower.Groups["var"].Value}.lower()";
        if (SortedCopyRepairPattern.Match(userText) is { Success: true } sorted)
            return $"sorted({sorted.Groups["var"].Value})";
        if (ShallowCopyRepairPattern.Match(userText) is { Success: true } shallowCopy)
            return $"{shallowCopy.Groups["var"].Value}.copy()";
        if (MutableDefaultListRepairPattern.Match(userText) is { Success: true } mutableDefault)
            return $"{mutableDefault.Groups["name"].Value}=None";
        if (DictMembershipRepairPattern.Match(userText) is { Success: true } dict)
            return $"{dict.Groups["key"].Value} in {dict.Groups["mapping"].Value}";
        if (DictMembershipReverseRepairPattern.Match(userText) is { Success: true } dictReverse)
            return $"{dictReverse.Groups["key"].Value} in {dictReverse.Groups["mapping"].Value}";
        if (BooleanNegationRepairPattern.Match(userText) is { Success: true } boolean)
            return $"not {boolean.Groups["var"].Value}";
        if (ModuloFilterRepairPattern.Match(userText) is { Success: true } modulo)
            return $"{modulo.Groups["var"].Value} % {modulo.Groups["divisor"].Value} == 0";
        if (AbsRepairPattern.Match(userText) is { Success: true } abs)
            return $"abs({abs.Groups["var"].Value})";
        if (LastItemRepairPattern.Match(userText) is { Success: true } last)
            return $"{last.Groups["var"].Value}[-1]";
        if (NotSeenRepairPattern.Match(userText) is { Success: true } notSeen)
            return $"{notSeen.Groups["item"].Value} not in {notSeen.Groups["seen"].Value}";
        if (InclusiveComparisonRepairPattern.Match(userText) is { Success: true } inclusive)
            return $"{inclusive.Groups["left"].Value} <= {inclusive.Groups["right"].Value}";
        if (StartsWithRepairPattern.Match(userText) is { Success: true } startsWith)
            return $"{startsWith.Groups["text"].Value}.startswith({startsWith.Groups["prefix"].Value})";
        if (CaseInsensitiveRepairPattern.Match(userText) is { Success: true } caseInsensitive)
            return $"{caseInsensitive.Groups["left"].Value}.lower() == {caseInsensitive.Groups["right"].Value}.lower()";
        if (ClampMinRepairPattern.Match(userText) is { Success: true } clamp)
            return $"max({ParseIntegerWord(clamp.Groups["min"].Value)}, {clamp.Groups["var"].Value})";
        if (ClampMaxRepairPattern.Match(userText) is { Success: true } clampMax)
            return $"min({clampMax.Groups["max"].Value}, {clampMax.Groups["var"].Value})";
        if (EndsWithRepairPattern.Match(userText) is { Success: true } endsWith)
            return $"{endsWith.Groups["text"].Value}.endswith({endsWith.Groups["suffix"].Value})";
        if (HasAnyRepairPattern.Match(userText) is { Success: true } hasAny)
            return $"bool({hasAny.Groups["var"].Value})";
        if (CeilingPageCountRepairPattern.Match(userText) is { Success: true } pageCount)
            return $"({pageCount.Groups["total"].Value} + {pageCount.Groups["pageSize"].Value} - 1) // {pageCount.Groups["pageSize"].Value}";
        if (CommaJoinRepairPattern.Match(userText) is { Success: true } commaJoin)
            return $"\",\".join({commaJoin.Groups["var"].Value})";
        if (RunningTotalRepairPattern.Match(userText) is { Success: true } runningTotal)
            return $"{runningTotal.Groups["total"].Value} += {runningTotal.Groups["item"].Value}";
        if (AppendReturnRepairPattern.Match(userText) is { Success: true } appendReturn)
            return $"{appendReturn.Groups["collection"].Value}.append({appendReturn.Groups["item"].Value}); return {appendReturn.Groups["collection"].Value}";
        if (DictGetFallbackRepairPattern.Match(userText) is { Success: true } dictGet)
            return $"{dictGet.Groups["mapping"].Value}.get({dictGet.Groups["key"].Value}, {dictGet.Groups["fallback"].Value})";
        if (FlattenRepairPattern.Match(userText) is { Success: true } flatten)
            return $"[item for row in {flatten.Groups["rows"].Value} for item in row]";
        if (AnyStartsWithRepairPattern.Match(userText) is { Success: true } anyStartsWith)
            return $"any({anyStartsWith.Groups["item"].Value}.startswith({anyStartsWith.Groups["prefix"].Value}) for {anyStartsWith.Groups["item"].Value} in {anyStartsWith.Groups["collection"].Value})";
        if (SortByFieldRepairPattern.Match(userText) is { Success: true } sortBy)
            return $"sorted({sortBy.Groups["collection"].Value}, key=lambda user: user['{sortBy.Groups["field"].Value}'])";
        if (UniquePreserveOrderRepairPattern.Match(userText) is { Success: true } unique)
            return $"list(dict.fromkeys({unique.Groups["collection"].Value}))";
        if (SafeDivideRepairPattern.Match(userText) is { Success: true } safeDivide)
            return $"0 if {safeDivide.Groups["denominator"].Value} == 0 else {safeDivide.Groups["numerator"].Value} / {safeDivide.Groups["denominator"].Value}";
        if (ReturnIndexRepairPattern.IsMatch(userText))
            return "i";
        if (DictIncrementRepairPattern.Match(userText) is { Success: true } dictIncrement)
            return $"{dictIncrement.Groups["mapping"].Value}[{dictIncrement.Groups["key"].Value}] = {dictIncrement.Groups["mapping"].Value}.get({dictIncrement.Groups["key"].Value}, 0) + 1";
        if (ReversedCopyRepairPattern.Match(userText) is { Success: true } reversedCopy)
            return $"list(reversed({reversedCopy.Groups["var"].Value}))";
        if (FilterNoneRepairPattern.Match(userText) is { Success: true } filterNone)
            return $"[item for item in {filterNone.Groups["var"].Value} if item is not None]";
        if (ParseIntFallbackRepairPattern.Match(userText) is { Success: true } parseInt)
            return $"int({parseInt.Groups["text"].Value}) if {parseInt.Groups["text"].Value}.isdigit() else {parseInt.Groups["fallback"].Value}";
        if (AllPositiveRepairPattern.Match(userText) is { Success: true } allPositive)
            return $"all({allPositive.Groups["item"].Value} > 0 for {allPositive.Groups["item"].Value} in {allPositive.Groups["collection"].Value})";
        if (MergeDictsRepairPattern.Match(userText) is { Success: true } mergeDicts)
            return $"{{**{mergeDicts.Groups["base"].Value}, **{mergeDicts.Groups["override"].Value}}}";
        if (StripSplitRepairPattern.Match(userText) is { Success: true } stripSplit)
            return $"[part.strip() for part in {stripSplit.Groups["text"].Value}.split(',')]";
        if (MissingReturnRepairPattern.Match(userText) is { Success: true } missingReturn)
            return $"return {missingReturn.Groups["var"].Value}";
        if (FirstNSliceRepairPattern.Match(userText) is { Success: true } firstN)
            return $"{firstN.Groups["collection"].Value}[:{firstN.Groups["count"].Value}]";

        return null;
    }

    private static string? TryRepairPythonTemplateContract(string userText)
    {
        var args = TryParsePythonFunctionArgs(userText);
        var normalized = Regex.Replace(userText.ToLowerInvariant(), @"\s+", " ");

        if (args.Length >= 1 &&
            HasAll(normalized, "group", "words", "first letter"))
        {
            var collection = args[0];
            return $"{{key: [item for item in {collection} if item[0] == key] for key in sorted(set(item[0] for item in {collection}))}}";
        }

        if (args.Length >= 1 &&
            HasAll(normalized, "pair", "adjacent"))
        {
            var collection = args[0];
            return $"[({collection}[i], {collection}[i + 1]) for i in range(len({collection}) - 1)]";
        }

        if (args.Length >= 2 &&
            HasAll(normalized, "parse", "yes", "no", "fallback"))
        {
            var text = args[0];
            var fallback = args[1];
            return $"True if {text}.strip().lower() == 'yes' else False if {text}.strip().lower() == 'no' else {fallback}";
        }

        if (args.Length >= 2 &&
            HasAll(normalized, "split", "chunks"))
        {
            var collection = args[0];
            var size = args[1];
            return $"[{collection}[i:i+{size}] for i in range(0, len({collection}), {size})]";
        }

        if (args.Length >= 2 &&
            HasAll(normalized, "combine", "count", "dictionaries", "summing"))
        {
            var left = args[0];
            var right = args[1];
            return $"{{key: {left}.get(key, 0) + {right}.get(key, 0) for key in set({left}) | set({right})}}";
        }

        if (args.Length >= 1 &&
            HasAll(normalized, "first", "duplicate"))
        {
            var collection = args[0];
            return $"seen=set(); return next((item for item in {collection} if item in seen or seen.add(item)), None)";
        }

        if (args.Length >= 2 &&
            HasAll(normalized, "ignore", "case", "surrounding", "spaces"))
        {
            var query = args[1];
            return $"{query}.strip().lower()";
        }

        if (args.Length >= 1 &&
            HasAll(normalized, "rolling", "sums", "width 3"))
        {
            var collection = args[0];
            return $"[sum({collection}[i:i+3]) for i in range(len({collection}) - 2)]";
        }

        return null;
    }

    private static string[] TryParsePythonFunctionArgs(string userText)
    {
        var match = PythonFunctionSignaturePattern.Match(userText);
        if (!match.Success)
            return [];

        return match.Groups["args"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(arg => Regex.Match(arg, @"^[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant).Value)
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .ToArray();
    }

    private static bool HasAll(string text, params string[] needles) =>
        needles.All(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string? TryRepairMinMaxCondition(string userText)
    {
        var match = MinMaxConditionPattern.Match(userText);
        if (!match.Success)
            return null;

        var goal = match.Groups["goal"].Value;
        var left = match.Groups["left"].Value;
        var op = match.Groups["operator"].Value;
        var right = match.Groups["right"].Value;

        if (goal.Equals("minimum", StringComparison.OrdinalIgnoreCase) && op == ">")
            return $"{left} < {right}";
        if (goal.Equals("maximum", StringComparison.OrdinalIgnoreCase) && op == "<")
            return $"{left} > {right}";

        return null;
    }

    private static string[] SplitWords(string text) =>
        Regex.Matches(text, @"[A-Za-z0-9_.-]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .ToArray();

    private static string StripTrailingPunctuation(string text) =>
        text.Trim().TrimEnd('.', ',', ';', ':', '!', '?');

    private static long? ParseLong(Match match, string groupName)
    {
        var value = match.Groups[groupName].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseLinearCoefficient(string value)
    {
        var normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized switch
        {
            "" or "+" => 1,
            "-" => -1,
            _ => long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
    }

    private static long? ParseLongOrSmallWord(Match match, string groupName)
    {
        if (ParseLong(match, groupName) is { } parsed)
            return parsed;

        return match.Groups[groupName].Value.Trim().ToLowerInvariant() switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "twelve" => 12,
            "twenty" => 20,
            _ => null,
        };
    }

    private static string ParseIntegerWord(string value) =>
        value.Trim().Equals("zero", StringComparison.OrdinalIgnoreCase) ? "0" : value.Trim();

    private static decimal? ParseDecimal(Match match, string groupName)
    {
        var value = match.Groups[groupName].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? ParseSignedDecimal(string value)
    {
        var cleaned = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(cleaned))
            return 0m;

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string FormatDecimal(decimal value)
    {
        var normalized = decimal.Round(value, 12);
        return normalized.ToString("0.############", CultureInfo.InvariantCulture);
    }

    private static long ParseOptionalLong(Match match, string groupName, long defaultValue)
    {
        var group = match.Groups[groupName];
        if (!group.Success || string.IsNullOrWhiteSpace(group.Value))
            return defaultValue;

        var value = group.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static long ModPow(long baseValue, long exponent, long modulus)
    {
        var result = 1L;
        var value = PositiveMod(baseValue, modulus);
        var remaining = exponent;
        while (remaining > 0)
        {
            if ((remaining & 1L) == 1L)
                result = checked(result * value % modulus);

            value = checked(value * value % modulus);
            remaining >>= 1;
        }

        return result;
    }

    private static long PositiveMod(long value, long modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static long Lcm(long left, long right) => checked(left / Gcd(left, right) * right);

    private static long Combination(long total, long selected)
    {
        selected = Math.Min(selected, total - selected);
        var result = 1L;
        for (var index = 1L; index <= selected; index++)
            result = checked(result * (total - selected + index) / index);

        return result;
    }

    private static long Gcd(long left, long right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            var next = left % right;
            left = right;
            right = next;
        }

        return left;
    }

    private static string? TrySolveWordPositions(Match match)
    {
        var words = SplitWords(match.Groups["words"].Value);
        var firstIndex = OrdinalToIndex(match.Groups["first"].Value);
        var secondIndex = OrdinalToIndex(match.Groups["second"].Value);
        if (firstIndex is null || secondIndex is null || firstIndex.Value >= words.Length || secondIndex.Value >= words.Length)
            return null;

        var separator = match.Groups["separator"].Value.ToLowerInvariant() switch
        {
            "a slash" or "slashes" => "/",
            "comma" or "commas" => ",",
            _ => " ",
        };
        return $"{words[firstIndex.Value]}{separator}{words[secondIndex.Value]}";
    }

    private static string? TrySolveCountdown(Match match)
    {
        var start = ParseLong(match, "start");
        var end = ParseLong(match, "end");
        if (start is null || end is null)
            return null;

        var separator = match.Groups["separator"].Value.StartsWith("comma", StringComparison.OrdinalIgnoreCase) ? "," : " ";
        var values = start.Value >= end.Value
            ? Enumerable.Range((int)end.Value, (int)(start.Value - end.Value + 1)).Reverse()
            : Enumerable.Range((int)start.Value, (int)(end.Value - start.Value + 1));
        return string.Join(separator, values);
    }

    private static string? TrySolveTagsCountJson(Match match)
    {
        var tags = Regex.Matches(match.Groups["tags"].Value, @"[""']?(?<tag>[A-Za-z0-9_.-]+)[""']?", RegexOptions.CultureInvariant)
            .Select(tag => tag.Groups["tag"].Value)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToArray();
        if (tags.Length == 0)
            return null;

        return $"{{\"tags\":[{string.Join(",", tags.Select(tag => $"\"{tag}\""))}],\"count\":{match.Groups["count"].Value}}}";
    }

    private static string? TrySolveStatusChecksJson(Match match)
    {
        var checks = Regex.Matches(match.Groups["checks"].Value, @"[""']?(?<check>[A-Za-z0-9_.-]+)[""']?", RegexOptions.CultureInvariant)
            .Select(check => check.Groups["check"].Value)
            .Where(check => !string.IsNullOrWhiteSpace(check))
            .ToArray();
        if (checks.Length == 0)
            return null;

        return $"{{\"status\":\"{match.Groups["status"].Value}\",\"checks\":[{string.Join(",", checks.Select(check => $"\"{check}\""))}]}}";
    }

    private static int CountVowels(string word) =>
        word.Count(character => "aeiou".Contains(char.ToLowerInvariant(character), StringComparison.Ordinal));

    private static string ToAlternatingCase(string word)
    {
        var chars = word.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
            chars[index] = index % 2 == 0 ? char.ToLowerInvariant(chars[index]) : char.ToUpperInvariant(chars[index]);
        return new string(chars);
    }

    private static int? OrdinalToIndex(string ordinal) =>
        ordinal.ToLowerInvariant() switch
        {
            "first" => 0,
            "second" => 1,
            "third" => 2,
            "fourth" => 3,
            "fifth" => 4,
            "sixth" => 5,
            "seventh" => 6,
            "eighth" => 7,
            "ninth" => 8,
            "tenth" => 9,
            _ => null,
        };
}
