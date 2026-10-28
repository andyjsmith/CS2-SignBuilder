import { useState } from "react";
import { deleteTemplate, openTemplatesFolder, renameTemplate, saveTemplate } from "./bindings";
import { Design } from "./designModel";
import { SignPreview } from "./SignPreview";
import { TextBox } from "./Controls";
import styles from "./SignEditor.module.scss";

export type Template = { id: string; name: string; builtIn?: boolean; design: Design };

/**
 * Template cards with a live thumbnail of each sign. Built-in templates ship with the mod (deleting one hides it);
 * the player's own are JSON files in ModsData/SignBuilder/Templates, easy to share.
 */
export const TemplateLibrary = (props: { templates: Template[]; current: Design; onPick: (t: Template) => void }) => {
  const [naming, setNaming] = useState<{ id?: string; name: string } | null>(null);
  const [menu, setMenu] = useState<string | null>(null);

  const commit = () => {
    if (!naming || !naming.name.trim()) { setNaming(null); return; }
    if (naming.id) renameTemplate(naming.id, naming.name.trim());
    else saveTemplate(naming.name.trim(), props.current);
    setNaming(null);
  };

  return (
    <div className={styles.library}>
      <div className={styles.libraryActions}>
        <div className={styles.chip} onClick={() => setNaming({ name: props.current.name && !props.current.id ? props.current.name : "My sign" })}>Save current as template</div>
        <div className={styles.chip} onClick={() => openTemplatesFolder()}>Open folder</div>
      </div>
      {naming && (
        <div className={styles.namingRow}>
          <TextBox value={naming.name} autoFocus onChange={(name) => setNaming({ ...naming, name })} />
          <div className={styles.chip} onClick={commit}>{naming.id ? "Rename" : "Save"}</div>
          <div className={styles.chip} onClick={() => setNaming(null)}>Cancel</div>
        </div>
      )}
      <div className={styles.cards}>
        {props.templates.map((t) => (
          <div key={t.id} className={styles.card} onClick={() => props.onPick(t)}>
            <div className={styles.thumb}>
              <SignPreview design={t.design} interactive={false} maxWidth={124} maxHeight={64} padding={6} />
            </div>
            <div className={styles.cardName}>{t.name}</div>
            <div className={styles.cardMenu} onClick={(e) => { e.stopPropagation(); setMenu(menu === t.id ? null : t.id); }}>···</div>
            {menu === t.id && (
              <div className={styles.cardActions} onClick={(e) => e.stopPropagation()}>
                {!t.builtIn && <div className={styles.chip} onClick={() => { setNaming({ id: t.id, name: t.name }); setMenu(null); }}>Rename</div>}
                {!t.builtIn && <div className={styles.chip} onClick={() => { saveTemplate(t.name, props.current, t.id); setMenu(null); }}>Overwrite with current</div>}
                <div className={styles.chip} onClick={() => { deleteTemplate(t.id); setMenu(null); }}>{t.builtIn ? "Hide" : "Delete"}</div>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
};
