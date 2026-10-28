namespace SignBuilder.Assemblies
{
    /// <summary>
    /// A support mesh as a recipe: generator name plus its arguments (see GantryFactory.FromSpec). Small
    /// enough to store in saves, and the mesh it produces is the same on every PC.
    /// </summary>
    public sealed class SupportSpec
    {
        public string Kind;
        public float[] Args;
        /// <summary>Mirror across X (cantilevers on the drivers' left).</summary>
        public bool Mirror;

        public static SupportSpec Of(string kind, params float[] args) => new SupportSpec { Kind = kind, Args = args };
    }
}
