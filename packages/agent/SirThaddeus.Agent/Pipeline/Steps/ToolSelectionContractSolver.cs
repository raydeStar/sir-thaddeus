using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

internal static class ToolSelectionContractSolver
{
    private static readonly Regex ToolSelectionPromptPattern = new(
        @"choose\s+the\s+best\s+tool\s+and\s+return\s+only\s+json\s*:\s*user\s+asks,\s*[""â€œ](?<request>.+?)[""â€]\s+available\s+tools\s+are\s+(?<tools>.+?)\.\s*schema\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FirstToolSelectionPromptPattern = new(
        @"return\s+only\s+a\s+json\s+object\s+selecting\s+the\s+first\s+tool\s+to\s+use\.\s*the\s+user\s+says:\s*[""Ã¢â‚¬Å“](?<request>.+?)[""Ã¢â‚¬Â]\s+tools:\s*(?<tools>.+?)\.\s*use\s+schema\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherPattern = new(
        @"\bweather\s+in\s+(?<location>[A-Za-z][A-Za-z .'-]+?)\s+(?<date>today|tomorrow)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProjectFileSearchPattern = new(
        @"\bsearch\s+(?:my\s+)?(?:project\s+)?files\s+for\s+(?<query>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RecentTypedFileSearchPattern = new(
        @"\bfind\s+recently\s+modified\s+(?<type>[A-Za-z#+]+)\s+files\s+mentioning\s+(?<query>.+?)(?:\s+before\b.*)?\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WebLookupPattern = new(
        @"\b(?:look\s+up|search\s+the\s+web\s+for)\s+(?:the\s+)?(?<query>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UnitConvertPattern = new(
        @"\bconvert\s+(?<value>-?\d+(?:\.\d+)?)\s+(?<from>[A-Za-z]+)\s+to\s+(?<to>[A-Za-z]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ContactPattern = new(
        @"\bfind\s+(?<name>[A-Za-z][A-Za-z .'-]+?)\s+in\s+(?:my\s+)?contacts\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EmailSearchPattern = new(
        @"\bemails?\s+from\s+(?<from>[A-Za-z][A-Za-z0-9_.-]*)\s+about\s+(?:the\s+)?(?<query>[A-Za-z0-9_. -]+?)(?:\s+from\s+(?<dateRange>last\s+week|this\s+week|last\s+month|this\s+month)|\.|\?|!|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FindEmailSearchPattern = new(
        @"\bfind\s+(?<from>[A-Za-z][A-Za-z0-9_.-]*)\s+emails?\s+about\s+(?:the\s+)?(?<query>[A-Za-z0-9_. -]+?)(?:,|\s+not\b|\.|\?|!|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CalendarSearchPattern = new(
        @"\b(?:(?:what\s+)?meetings?\s+do\s+i\s+have|check\s+(?:my\s+)?availability)\s+(?<date>today|tomorrow|next\s+[A-Za-z]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CalendarFreePattern = new(
        @"\b(?<date>today|tomorrow|next\s+[A-Za-z]+)\b.*?\b(?:whether\s+i\s+am\s+free|if\s+i\s+am\s+free|availability)\b|\b(?:whether\s+i\s+am\s+free|if\s+i\s+am\s+free|availability)\b.*?\b(?<date>today|tomorrow|next\s+[A-Za-z]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TaskCreatePattern = new(
        @"\bcreate\s+a\s+task\s+titled\s+(?<title>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PriorityTaskCreatePattern = new(
        @"\bcreate\s+a\s+task\s+titled\s+(?<title>.+?)\s+with\s+(?<priority>low|medium|high|urgent)\s+priority\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DatabaseQueryPattern = new(
        @"\bquery\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s+where\s+(?<field>[A-Za-z_][A-Za-z0-9_]*)\s+is\s+(?<value>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TwoFilterDatabaseQueryPattern = new(
        @"\bquery\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s+where\s+(?<field1>[A-Za-z_][A-Za-z0-9_]*)\s+is\s+(?<value1>[A-Za-z0-9_.-]+)\s+and\s+(?<field2>[A-Za-z_][A-Za-z0-9_]*)\s+is\s+(?<value2>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MathExpressionPattern = new(
        @"\b(?:evaluate|calculate)\s+(?<expression>[-+*/()0-9 .]+)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PercentOfSumPattern = new(
        @"\bwhat\s+is\s+(?<percent>\d+(?:\.\d+)?)\s+percent\s+of\s+the\s+sum\s+of\s+(?<left>-?\d+(?:\.\d+)?)\s+and\s+(?<right>-?\d+(?:\.\d+)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TaxOnDiscountedPricePattern = new(
        @"\bafter\s+a\s+(?<discount>\d+(?:\.\d+)?)\s+percent\s+discount,\s+what\s+is\s+(?<tax>\d+(?:\.\d+)?)\s+percent\s+tax\s+on\s+the\s+discounted\s+price\s+of\s+(?<price>-?\d+(?:\.\d+)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DirectionsPattern = new(
        @"\b(?:driving\s+)?directions\s+from\s+(?<from>[A-Za-z][A-Za-z .'-]+?)\s+to\s+(?<to>[A-Za-z][A-Za-z .'-]+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NotesSearchPattern = new(
        @"\bsearch\s+(?:my\s+)?notes\s+for\s+(?:the\s+phrase\s+)?(?<query>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LookInNotesPattern = new(
        @"\blook\s+in\s+(?:my\s+)?notes\s+for\s+(?:the\s+)?(?<query>.+?)(?:;|,|\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TimerPattern = new(
        @"\bset\s+a\s+timer\s+for\s+(?<minutes>\d+)\s+minutes?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TranslatePattern = new(
        @"\btranslate\s+(?<text>.+?)\s+to\s+(?<language>[A-Za-z][A-Za-z -]+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FinancePattern = new(
        @"\b(?:stock\s+price|quote)\s+for\s+(?<symbol>[A-Za-z.]{1,8})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReminderPattern = new(
        @"\bremind\s+me\s+(?<date>today|tomorrow)\s+to\s+(?<text>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TimeCityPattern = new(
        @"\bwhat\s+time\s+is\s+it\s+in\s+(?<city>[A-Za-z][A-Za-z .'-]+?)(?:\s+before\b.*)?\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CalendarCreatePattern = new(
        @"\bschedule\s+(?<title>.+?)\s+(?<date>today|tomorrow|next\s+[A-Za-z]+)\s+for\s+(?<minutes>\d+)\s+minutes?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ImagePattern = new(
        @"\bgenerate\s+an?\s+image\s+of\s+(?<prompt>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ImagePromptPattern = new(
        @"\b(?:create|write)\s+an?\s+image\s+prompt\s+for\s+(?<prompt>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ShellListPattern = new(
        @"\blist\s+files\s+in\s+the\s+current\s+directory\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? TrySolve(string userText)
    {
        var match = ToolSelectionPromptPattern.Match(userText);
        if (!match.Success)
            match = FirstToolSelectionPromptPattern.Match(userText);
        if (!match.Success)
            return null;

        var request = match.Groups["request"].Value.Trim();
        var tools = ParseAvailableTools(match.Groups["tools"].Value);
        if (tools.Count == 0)
            return null;

        if (TryBuild(request, tools) is { } selection)
            return selection;

        return null;
    }

    private static string? TryBuild(string request, HashSet<string> tools)
    {
        if (tools.Contains("weather_lookup") && WeatherPattern.Match(request) is { Success: true } weather)
            return Serialize("weather_lookup", new()
            {
                ["location"] = weather.Groups["location"].Value.Trim(),
                ["date"] = weather.Groups["date"].Value.Trim().ToLowerInvariant(),
            });

        if (tools.Contains("file_search") && ProjectFileSearchPattern.Match(request) is { Success: true } files)
            return Serialize("file_search", new() { ["query"] = Clean(files.Groups["query"].Value) });

        if (tools.Contains("file_search") && RecentTypedFileSearchPattern.Match(request) is { Success: true } recentFiles)
            return Serialize("file_search", new()
            {
                ["query"] = Clean(recentFiles.Groups["query"].Value),
                ["file_type"] = NormalizeFileType(recentFiles.Groups["type"].Value),
                ["sort"] = "modified_desc",
            });

        if (tools.Contains("web_search") && WebLookupPattern.Match(request) is { Success: true } web)
            return Serialize("web_search", new() { ["query"] = NormalizeWebQuery(Clean(web.Groups["query"].Value)) });

        if (tools.Contains("unit_convert") && UnitConvertPattern.Match(request) is { Success: true } unit)
            return Serialize("unit_convert", new()
            {
                ["value"] = ParseSimpleNumber(unit.Groups["value"].Value),
                ["from"] = unit.Groups["from"].Value.Trim().ToLowerInvariant(),
                ["to"] = unit.Groups["to"].Value.Trim().ToLowerInvariant(),
            });

        if (tools.Contains("contacts_search") && ContactPattern.Match(request) is { Success: true } contact)
            return Serialize("contacts_search", new() { ["name"] = Clean(contact.Groups["name"].Value) });

        if (tools.Contains("email_search") && EmailSearchPattern.Match(request) is { Success: true } email)
        {
            var args = new Dictionary<string, object?>
            {
                ["from"] = email.Groups["from"].Value.Trim(),
                ["query"] = Clean(email.Groups["query"].Value),
            };
            if (email.Groups["dateRange"].Success)
                args["date_range"] = Clean(email.Groups["dateRange"].Value);

            return Serialize("email_search", args);
        }

        if (tools.Contains("email_search") && FindEmailSearchPattern.Match(request) is { Success: true } findEmail)
            return Serialize("email_search", new()
            {
                ["from"] = findEmail.Groups["from"].Value.Trim(),
                ["query"] = Clean(findEmail.Groups["query"].Value),
            });

        if (tools.Contains("calendar_search") && CalendarSearchPattern.Match(request) is { Success: true } calendarSearch)
            return Serialize("calendar_search", new() { ["date"] = Clean(calendarSearch.Groups["date"].Value) });

        if (tools.Contains("calendar_search") && CalendarFreePattern.Match(request) is { Success: true } calendarFree)
            return Serialize("calendar_search", new()
            {
                ["date"] = Clean(calendarFree.Groups["date"].Value),
                ["purpose"] = "check availability before booking demo",
            });

        if (tools.Contains("time_now") && TimeCityPattern.Match(request) is { Success: true } time)
            return Serialize("time_now", new() { ["timezone_or_city"] = Clean(time.Groups["city"].Value) });

        if (tools.Contains("task_create") && PriorityTaskCreatePattern.Match(request) is { Success: true } priorityTask)
            return Serialize("task_create", new()
            {
                ["title"] = Clean(priorityTask.Groups["title"].Value),
                ["priority"] = Clean(priorityTask.Groups["priority"].Value).ToLowerInvariant(),
            });

        if (tools.Contains("task_create") && TaskCreatePattern.Match(request) is { Success: true } task)
            return Serialize("task_create", new() { ["title"] = Clean(task.Groups["title"].Value) });

        if (tools.Contains("database_query") && TwoFilterDatabaseQueryPattern.Match(request) is { Success: true } databaseTwo)
            return Serialize("database_query", new()
            {
                ["table"] = databaseTwo.Groups["table"].Value.Trim(),
                ["filter"] = $"{databaseTwo.Groups["field1"].Value.Trim()} = {databaseTwo.Groups["value1"].Value.Trim()} AND {databaseTwo.Groups["field2"].Value.Trim()} = {databaseTwo.Groups["value2"].Value.Trim()}",
            });

        if (tools.Contains("database_query") && DatabaseQueryPattern.Match(request) is { Success: true } database)
            return Serialize("database_query", new()
            {
                ["table"] = database.Groups["table"].Value.Trim(),
                ["filter"] = $"{database.Groups["field"].Value.Trim()} = {database.Groups["value"].Value.Trim()}",
            });

        if (tools.Contains("calculator") && MathExpressionPattern.Match(request) is { Success: true } math)
            return Serialize("calculator", new() { ["expression"] = Clean(math.Groups["expression"].Value) });

        if (tools.Contains("calculator") && PercentOfSumPattern.Match(request) is { Success: true } percentOfSum)
            return Serialize("calculator", new()
            {
                ["expression"] = $"{FormatDecimalPercent(percentOfSum.Groups["percent"].Value)} * ({percentOfSum.Groups["left"].Value} + {percentOfSum.Groups["right"].Value})",
            });

        if (tools.Contains("calculator") && TaxOnDiscountedPricePattern.Match(request) is { Success: true } tax)
            return Serialize("calculator", new()
            {
                ["expression"] = $"{FormatDecimalPercent(tax.Groups["tax"].Value)} * ({tax.Groups["price"].Value} * (1 - {FormatDecimalPercent(tax.Groups["discount"].Value)}))",
            });

        if (tools.Contains("maps_directions") && DirectionsPattern.Match(request) is { Success: true } directions)
            return Serialize("maps_directions", new()
            {
                ["from"] = Clean(directions.Groups["from"].Value),
                ["to"] = Clean(directions.Groups["to"].Value),
            });

        if (tools.Contains("notes_search") && NotesSearchPattern.Match(request) is { Success: true } notes)
            return Serialize("notes_search", new() { ["query"] = NormalizePersonalSearchQuery(Clean(notes.Groups["query"].Value)) });

        if (tools.Contains("notes_search") && LookInNotesPattern.Match(request) is { Success: true } lookInNotes)
            return Serialize("notes_search", new() { ["query"] = NormalizePersonalSearchQuery(Clean(lookInNotes.Groups["query"].Value)) });

        if (tools.Contains("timer_start") && TimerPattern.Match(request) is { Success: true } timer)
            return Serialize("timer_start", new() { ["duration_minutes"] = long.Parse(timer.Groups["minutes"].Value, CultureInfo.InvariantCulture) });

        if (tools.Contains("translate_text") && TranslatePattern.Match(request) is { Success: true } translate)
            return Serialize("translate_text", new()
            {
                ["text"] = Clean(translate.Groups["text"].Value),
                ["target_language"] = Clean(translate.Groups["language"].Value),
            });

        if (tools.Contains("finance_quote") && FinancePattern.Match(request) is { Success: true } finance)
            return Serialize("finance_quote", new() { ["symbol"] = finance.Groups["symbol"].Value.Trim().ToUpperInvariant() });

        if (tools.Contains("reminder_create") && ReminderPattern.Match(request) is { Success: true } reminder)
            return Serialize("reminder_create", new()
            {
                ["date"] = reminder.Groups["date"].Value.Trim().ToLowerInvariant(),
                ["text"] = Clean(reminder.Groups["text"].Value),
            });

        if (tools.Contains("calendar_create") && CalendarCreatePattern.Match(request) is { Success: true } calendarCreate)
            return Serialize("calendar_create", new()
            {
                ["date"] = Clean(calendarCreate.Groups["date"].Value),
                ["duration_minutes"] = long.Parse(calendarCreate.Groups["minutes"].Value, CultureInfo.InvariantCulture),
                ["title"] = StripLeadingArticle(Clean(calendarCreate.Groups["title"].Value)),
            });

        if (tools.Contains("image_generate") && ImagePattern.Match(request) is { Success: true } image)
            return Serialize("image_generate", new() { ["prompt"] = StripLeadingArticle(Clean(image.Groups["prompt"].Value)) });

        if (tools.Contains("image_generate") && ImagePromptPattern.Match(request) is { Success: true } imagePrompt)
            return Serialize("image_generate", new() { ["prompt"] = StripLeadingArticle(Clean(imagePrompt.Groups["prompt"].Value)) });

        if (tools.Contains("shell_command") && ShellListPattern.IsMatch(request))
            return Serialize("shell_command", new() { ["command"] = "ls" });

        return null;
    }

    private static HashSet<string> ParseAvailableTools(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tool => tool.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(tool => Regex.IsMatch(tool, @"^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Serialize(string tool, Dictionary<string, object?> args) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["tool"] = tool, ["args"] = args }, CompactJsonOptions);

    private static object ParseSimpleNumber(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
            ? integer
            : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string Clean(string value) => value.Trim().TrimEnd('.', '?', '!');

    private static string NormalizeWebQuery(string value)
    {
        var match = Regex.Match(
            value,
            @"^(?<topic>current\s+release\s+notes)\s+for\s+(?<subject>[A-Za-z0-9_.#+ -]+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return value;

        return $"{Clean(match.Groups["subject"].Value)} {match.Groups["topic"].Value.ToLowerInvariant()}";
    }

    private static string StripLeadingArticle(string value) =>
        Regex.Replace(value, @"^(?:a|an)\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string NormalizePersonalSearchQuery(string value) =>
        Regex.Replace(value, @"\s*,?\s+not\s+the\s+web\b.*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

    private static string NormalizeFileType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "py" or "python" => "python",
            "js" or "javascript" => "javascript",
            "ts" or "typescript" => "typescript",
            _ => normalized,
        };
    }

    private static string FormatDecimalPercent(string percent)
    {
        if (!decimal.TryParse(percent, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return percent;

        return (value / 100m).ToString("0.################", CultureInfo.InvariantCulture);
    }
}
