import { ModRegistrar } from "cs2/modding";
import { SignEditor } from "mods/SignEditor";
import { ToolbarButton } from "mods/ToolbarButton";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", ToolbarButton);
  moduleRegistry.append("Game", SignEditor);
};

export default register;
