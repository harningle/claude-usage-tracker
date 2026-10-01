namespace ClaudeUsageTracker.Core.Profiles;

/// <summary>Random default profile names. Port of upstream <c>FunnyNameGenerator</c>, verbatim list.</summary>
public static class FunnyNames
{
    public static readonly IReadOnlyList<string> Names =
    [
        "Quantum Llama", "Sneaky Penguin", "Turbo Sloth", "Cosmic Cat",
        "Digital Dragon", "Ninja Narwhal", "Pixel Panda", "Rocket Raccoon",
        "Thunder Turtle", "Wizard Wombat", "Electric Eel", "Funky Falcon",
        "Galaxy Gopher", "Happy Hippo", "Jazzy Jaguar", "Laser Lemur",
        "Mystic Moose", "Neon Newt", "Psychic Puffin", "Quirky Quokka",
        "Rainbow Rhino", "Stellar Seahorse", "Techno Tiger", "Ultra Unicorn",
        "Vibrant Viper", "Wild Walrus", "Xenon Xerus", "Yolo Yak",
        "Zippy Zebra", "Awesome Axolotl",
    ];

    /// <summary>
    /// A name not in <paramref name="usedNames"/> (exact, case-sensitive), or "Profile NNNN" when all are
    /// used. As upstream, the fallback is not checked against existing names.
    /// </summary>
    public static string Random(IEnumerable<string> usedNames, Random? random = null)
    {
        random ??= System.Random.Shared;
        var used = usedNames.ToHashSet(StringComparer.Ordinal);
        var available = Names.Where(n => !used.Contains(n)).ToList();
        return available.Count > 0 ? available[random.Next(available.Count)] : $"Profile {random.Next(1000, 10000)}";
    }
}
