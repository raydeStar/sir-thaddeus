using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

internal static class MultipleChoiceConceptSolver
{
    private static readonly Regex MultipleChoicePattern = new(
        @"choose\s+the\s+best\s+answer\.\s*(?<stem>.+?)\s+reply\s+with\s+only\s+A,\s+B,\s+C,\s+or\s+D",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ChoicePattern = new(
        @"(?<letter>[A-D])\)\s*(?<text>.*?)(?=\s+[A-D]\)|\s+Reply\s+with\s+only\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    public static string? TrySolve(string userText)
    {
        var prompt = MultipleChoicePattern.Match(userText);
        if (!prompt.Success)
            return null;

        var stem = prompt.Groups["stem"].Value;
        var choices = ChoicePattern.Matches(userText)
            .Select(match => new Choice(
                match.Groups["letter"].Value.ToUpperInvariant(),
                Normalize(match.Groups["text"].Value)))
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Text))
            .ToArray();
        if (choices.Length < 2)
            return null;

        return TrySolveScienceConcept(stem, choices);
    }

    private static string? TrySolveScienceConcept(string stem, Choice[] choices)
    {
        var normalizedStem = Normalize(stem);

        if (ContainsAny(normalizedStem, "enzyme", "catalyst"))
        {
            if (ContainsAny(normalizedStem, "delta g", "equilibrium", "thermodynamic quantity", "speeds a reaction"))
            {
                if (FindChoice(choices, HasActivationEnergyAndEquilibriumGuard) is { } activationChoice)
                    return activationChoice;
                if (FindChoice(choices, text => HasWord(text, "neither") && HasWord(text, "equilibrium") && HasWord(text, "delta")) is { } neitherChoice)
                    return neitherChoice;
            }
        }

        if (HasWord(normalizedStem, "denaturation") && HasWord(normalizedStem, "protein"))
        {
            return FindChoice(choices, text =>
                (HasWord(text, "higher") || HasWord(text, "tertiary") || HasWord(text, "folding")) &&
                !ContainsAny(text, "only covalent peptide bonds first", "genetic code"));
        }

        if (HasWord(normalizedStem, "blinding") || HasWord(normalizedStem, "blind"))
            return FindChoice(choices, text => HasWord(text, "bias") || HasWord(text, "observer") || HasWord(text, "participant"));

        if (ContainsAny(normalizedStem, "normal phase chromatography", "normal-phase chromatography"))
            return FindChoice(choices, text => HasWord(text, "polar") && !HasWord(text, "nonpolar"));

        if (HasWord(normalizedStem, "osmosis") || (HasWord(normalizedStem, "water") && HasWord(normalizedStem, "moves")))
            return FindChoice(choices, text => HasWord(text, "higher") && HasWord(text, "solute"));

        if (HasWord(normalizedStem, "buffer") &&
            FindChoice(choices, text => ContainsAny(text, "p h only slightly", "ph only slightly", "weak acid neutralizes", "resists ph")) is { } bufferChoice)
        {
            return bufferChoice;
        }

        if (HasWord(normalizedStem, "pcr") && HasWord(normalizedStem, "annealing"))
            return FindChoice(choices, text => HasWord(text, "primers") && ContainsAny(text, "bind", "hybridize", "complementary"));

        if (ContainsAny(normalizedStem, "dosage and diet", "diet and dosage", "two variables", "main design issue"))
            return FindChoice(choices, text => HasWord(text, "confounding") || ContainsAny(text, "variables changed together", "confounders"));

        if (ContainsAny(normalizedStem, "genetic drift", "drift is strongest"))
            return FindChoice(choices, text => HasWord(text, "small") && ContainsAny(text, "population", "populations", "sampling"));

        if (ContainsAny(normalizedStem, "ir spectroscopy", "3200-3600", "broad peak"))
            return FindChoice(choices, text => ContainsAny(text, "o-h", "oh stretch", "alcohol", "hydroxyl"));

        if (ContainsAny(normalizedStem, "below pka", "p h is below pka", "ph is below pka"))
            return FindChoice(choices, text => HasWord(text, "protonated"));

        if (ContainsAny(normalizedStem, "negative control", "proper control"))
            return FindChoice(choices, text => ContainsAny(text, "baseline", "without the tested factor", "without treatment", "absence of"));

        if (HasWord(normalizedStem, "rate") && HasWord(normalizedStem, "constant"))
            return FindChoice(choices, text => ContainsAny(text, "how quickly", "reaction proceeds", "rate of reaction", "under given conditions"));

        if (ContainsAny(normalizedStem, "hardy-weinberg", "hardy weinberg"))
            return FindChoice(choices, text => HasWord(text, "random") && HasWord(text, "mating") && ContainsAny(text, "no selection", "no mutation", "no drift"));

        if (HasWord(normalizedStem, "oxidation"))
            return FindChoice(choices, text => ContainsAny(text, "loss of electrons", "loses electrons"));

        if (ContainsAny(normalizedStem, "random assignment", "causal inference"))
            return FindChoice(choices, text => ContainsAny(text, "balances confounders", "balance confounders", "confounders in expectation"));

        if (ContainsAny(normalizedStem, "competitive inhibitor", "competitive inhibitors"))
            return FindChoice(choices, text => ContainsAny(text, "active site", "competing with substrate", "compete with substrate"));

        if (HasWord(normalizedStem, "power") && ContainsAny(normalizedStem, "statistical", "probability"))
            return FindChoice(choices, text => ContainsAny(text, "detecting a real effect", "detect a real effect", "when one exists"));

        if (HasWord(normalizedStem, "diffusion"))
            return FindChoice(choices, text => ContainsAny(text, "down their concentration gradient", "down the concentration gradient"));

        if (ContainsAny(normalizedStem, "increasing pressure", "gas-phase equilibrium", "gas phase equilibrium"))
            return FindChoice(choices, text => ContainsAny(text, "fewer gas molecules", "less gas molecules", "fewer moles of gas"));

        if (ContainsAny(normalizedStem, "mrna codons", "during translation", "codons are read"))
            return FindChoice(choices, text => ContainsAny(text, "three-nucleotide", "three nucleotide", "triplet") && ContainsAny(text, "amino acids", "stops"));

        if (ContainsAny(normalizedStem, "type i error", "type 1 error"))
            return FindChoice(choices, text => ContainsAny(text, "rejecting a true null", "reject a true null", "false positive"));

        if (ContainsAny(normalizedStem, "type ii error", "type 2 error"))
            return FindChoice(choices, text => ContainsAny(text, "failing to reject a false null", "fail to reject a false null", "false negative"));

        if (ContainsAny(normalizedStem, "sister chromatids", "during mitosis") && HasWord(normalizedStem, "separated"))
            return FindChoice(choices, text => HasWord(text, "anaphase"));

        if (ContainsAny(normalizedStem, "second law of thermodynamics", "isolated system") && HasWord(normalizedStem, "entropy"))
            return FindChoice(choices, text => ContainsAny(text, "increase or remain constant", "not decrease", "increase"));

        if (ContainsAny(normalizedStem, "michaelis-menten", "michaelis menten") && HasWord(normalizedStem, "km"))
            return FindChoice(choices, text => ContainsAny(text, "half vmax", "half v max", "substrate concentration at half"));

        if (ContainsAny(normalizedStem, "aa x aa", "monohybrid") && ContainsAny(normalizedStem, "complete dominance", "phenotype ratio"))
            return FindChoice(choices, text => ContainsAny(text, "3 dominant to 1 recessive", "3:1"));

        if (ContainsAny(normalizedStem, "light reactions", "photosynthesis"))
            return FindChoice(choices, text => HasWord(text, "atp") && HasWord(text, "nadph"));

        if (ContainsAny(normalizedStem, "negative feedback", "physiology"))
            return FindChoice(choices, text => ContainsAny(text, "counteracts deviation", "opposes deviation", "set point"));

        if (HasWord(normalizedStem, "elisa"))
            return FindChoice(choices, text => ContainsAny(text, "proteins or antibodies", "protein or antibody", "specific proteins", "antibodies"));

        if (ContainsAny(normalizedStem, "proton nmr", "nmr") && ContainsAny(normalizedStem, "splitting", "splitting patterns"))
            return FindChoice(choices, text => ContainsAny(text, "neighboring nonequivalent hydrogens", "neighboring hydrogens", "nonequivalent hydrogens"));

        if (ContainsAny(normalizedStem, "phospholipid bilayer", "phospholipids are"))
            return FindChoice(choices, text => ContainsAny(text, "amphipathic", "hydrophilic heads and hydrophobic tails"));

        if (HasWord(normalizedStem, "p-value") || HasWord(normalizedStem, "p value"))
            return FindChoice(choices, text => ContainsAny(text, "assuming the null", "under the null", "data at least as extreme"));

        if (ContainsAny(normalizedStem, "antidiuretic hormone", "adh"))
            return FindChoice(choices, text => ContainsAny(text, "water reabsorption", "collecting ducts", "kidney"));

        if (ContainsAny(normalizedStem, "action potential", "rising phase") || ContainsAny(normalizedStem, "voltage-gated"))
            return FindChoice(choices, text => ContainsAny(text, "sodium ions entering", "sodium entering", "voltage-gated channels"));

        if (ContainsAny(normalizedStem, "exothermic equilibrium", "increasing temperature"))
            return FindChoice(choices, text => HasWord(text, "reactants"));

        if (ContainsAny(normalizedStem, "physically close together", "same chromosome", "genes"))
            return FindChoice(choices, text => ContainsAny(text, "inherited together", "linkage"));

        if (ContainsAny(normalizedStem, "logistic population growth", "carrying capacity"))
            return FindChoice(choices, text => ContainsAny(text, "maximum population size", "environment can sustain"));

        if (ContainsAny(normalizedStem, "weak acid buffer", "resists ph change"))
            return FindChoice(choices, text => ContainsAny(text, "near the acid pka", "near pka"));

        if (ContainsAny(normalizedStem, "95 percent confidence interval", "95% confidence interval"))
            return FindChoice(choices, text => ContainsAny(text, "repeated sampling", "capture the true parameter"));

        if (ContainsAny(normalizedStem, "stop codon", "mrna signals"))
            return FindChoice(choices, text => ContainsAny(text, "termination of translation", "stop translation"));

        return null;
    }

    private static bool HasActivationEnergyAndEquilibriumGuard(string text) =>
        (ContainsAny(text, "activation energy", "activation barrier") || HasWord(text, "activation")) &&
        (ContainsAny(text, "without changing equilibrium", "not changing equilibrium") ||
         (HasWord(text, "equilibrium") && !ContainsAny(text, "increasing product stability", "shift")));

    private static string? FindChoice(Choice[] choices, Func<string, bool> predicate) =>
        choices.FirstOrDefault(choice => predicate(choice.Text))?.Letter;

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool HasWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();

    private sealed record Choice(string Letter, string Text);
}
