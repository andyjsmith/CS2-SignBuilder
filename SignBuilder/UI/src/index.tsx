import { ModRegistrar } from "cs2/modding";
import { SignEditor } from "mods/SignEditor";
import { ToolbarButton } from "mods/ToolbarButton";
import { SignInfoSection } from "mods/SignInfoSection";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", ToolbarButton);
  moduleRegistry.append("Game", SignEditor);
  // C# SignBuilder.Systems.SignInfoSection writes sections under its full type name.
  moduleRegistry.extend(
    "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx",
    "selectedInfoSectionComponents",
    (components: any) => {
      components["SignBuilder.Systems.SignInfoSection"] = SignInfoSection;
      return components;
    },
  );
};

export default register;
