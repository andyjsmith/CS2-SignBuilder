import { getModule } from "cs2/modding";
import { Button } from "cs2/ui";
import { editSelected } from "./bindings";
import { SUPPORTS } from "./designModel";

// Vanilla selected-info building blocks; plain divs if a game update moves them.
const load = (path: string, name: string): any => {
  try { return getModule(path, name); } catch { return null; }
};
const InfoSection = load("game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx", "InfoSection");
const InfoRow = load("game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx", "InfoRow");

/** Selected-object panel section for placed Sign Builder structures (C#: Systems/SignInfoSection.cs). */
export const SignInfoSection = (props: { support?: string; signs?: number }) => {
  const mounting = SUPPORTS.find((s) => s.id === props.support)?.label ?? props.support ?? "";
  const summary = `${mounting}${props.signs && props.signs > 1 ? `, ${props.signs} signs` : ""}`;
  const button = (
    <div style={{ padding: "6rem 0" }}>
      <Button variant="flat" onSelect={() => editSelected()}>
        <div style={{ padding: "4rem 12rem", fontSize: "14rem" }}>Edit in Sign Builder</div>
      </Button>
    </div>
  );
  if (!InfoSection || !InfoRow) {
    return <div style={{ padding: "8rem" }}><div>{`Sign Builder: ${summary}`}</div>{button}</div>;
  }
  return (
    <InfoSection disableFocus>
      <InfoRow left="Sign Builder" right={summary} uppercase />
      <InfoRow left={button} />
    </InfoSection>
  );
};
