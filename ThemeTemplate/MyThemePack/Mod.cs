using Game;
using Game.Modding;

namespace MyThemePack
{
    /// <summary>
    /// Nothing to run: Sign Builder reads this mod's SignBuilderThemes folder. The game needs a mod class to load
    /// the mod (and so its files).
    /// </summary>
    public class Mod : IMod
    {
        public void OnLoad(UpdateSystem updateSystem)
        {
        }

        public void OnDispose()
        {
        }
    }
}
