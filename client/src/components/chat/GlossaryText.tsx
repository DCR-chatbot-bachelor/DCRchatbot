import { Fragment, type ReactNode } from "react";
import glossaryData from "../../data/glossary.json";

interface GlossaryEntry {
  term: string;
  aliases?: string[];
  definition: string;
}

const entries = glossaryData as GlossaryEntry[];

/** Escape tegn, så et fagbegreb kan indgå sikkert i et regulært udtryk. */
function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// Hvert fagbegreb kan skrives på flere måder (aliasser). Vi slår hver skrivemåde
// op til dens definition og bygger ét regulært udtryk, der finder dem alle.
const definitionByLabel = new Map<string, string>();
for (const entry of entries) {
  for (const label of [entry.term, ...(entry.aliases ?? [])]) {
    definitionByLabel.set(label.toLowerCase(), entry.definition);
  }
}

// Længste skrivemåder først, så fx "SU-lån" matches før "SU", og "CPR-nummer"
// før "CPR". Bogstavgrænserne (\p{L}\p{N}) sikrer, at vi kun rammer hele ord og
// ikke "SU" inde i "status".
const labels = [...definitionByLabel.keys()].sort((a, b) => b.length - a.length);
const pattern =
  labels.length > 0
    ? new RegExp(
        `(?<![\\p{L}\\p{N}])(${labels.map(escapeRegExp).join("|")})(?![\\p{L}\\p{N}])`,
        "giu",
      )
    : null;

interface GlossaryTermProps {
  label: string;
  definition: string;
}

function GlossaryTerm({ label, definition }: GlossaryTermProps) {
  return (
    <span
      className="glossary-term"
      tabIndex={0}
      role="button"
      aria-label={`${label}: ${definition}`}
    >
      {label}
      <span className="glossary-term__tooltip" role="tooltip">
        <strong className="glossary-term__title">{label}</strong>
        {definition}
      </span>
    </span>
  );
}

interface GlossaryTextProps {
  text: string;
}

/**
 * Viser en tekst, hvor kendte fagbegreber (se glossary.json) fremhæves og får en
 * lille forklaring, når borgeren holder musen over eller tabber hen til dem.
 */
export function GlossaryText({ text }: GlossaryTextProps) {
  if (!pattern) {
    return <>{text}</>;
  }

  const nodes: ReactNode[] = [];
  let lastIndex = 0;

  for (const match of text.matchAll(pattern)) {
    const matched = match[0];
    const start = match.index ?? 0;

    if (start > lastIndex) {
      nodes.push(text.slice(lastIndex, start));
    }

    const definition = definitionByLabel.get(matched.toLowerCase());
    if (definition) {
      nodes.push(
        <GlossaryTerm
          key={`${start}-${matched}`}
          label={matched}
          definition={definition}
        />,
      );
    } else {
      nodes.push(matched);
    }

    lastIndex = start + matched.length;
  }

  if (lastIndex < text.length) {
    nodes.push(text.slice(lastIndex));
  }

  return (
    <>
      {nodes.map((node, index) => (
        <Fragment key={index}>{node}</Fragment>
      ))}
    </>
  );
}
