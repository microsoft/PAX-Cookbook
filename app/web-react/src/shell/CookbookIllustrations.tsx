/**
 * Decorative line-art illustrations and the icon set for the desktop
 * workspace surfaces (Home dashboard and Recipes workspace).
 *
 * Everything here is presentational SVG — no data, no behavior, no network.
 * The illustrations carry the cookbook metaphor (mixing bowl, whisk, recipe
 * book, sparkles) in the brand blue so the redesigned surfaces feel like a
 * desktop kitchen workspace rather than a long web page.
 */
import type { CSSProperties, ReactNode } from 'react';

interface GlyphProps {
  className?: string;
  title?: string;
  style?: CSSProperties;
}

function Svg({
  children,
  className,
  title,
  style,
  viewBox = '0 0 24 24',
}: GlyphProps & { children: ReactNode; viewBox?: string }) {
  return (
    <svg
      className={className}
      style={style}
      viewBox={viewBox}
      width="1em"
      height="1em"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.7}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden={title ? undefined : true}
      role={title ? 'img' : undefined}
      focusable="false"
    >
      {title ? <title>{title}</title> : null}
      {children}
    </svg>
  );
}

/* ---------------------------------------------------------------- */
/* Command + control icons                                          */
/* ---------------------------------------------------------------- */

export const IconPlus = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M12 5v14M5 12h14" />
  </Svg>
);

export const IconChevronDown = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="m6 9 6 6 6-6" />
  </Svg>
);

export const IconChevronRight = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="m9 6 6 6-6 6" />
  </Svg>
);

export const IconFolder = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M3 7a2 2 0 0 1 2-2h4l2 2h6a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
  </Svg>
);

export const IconShieldCheck = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M12 3 5 6v5c0 4 3 7 7 9 4-2 7-5 7-9V6z" />
    <path d="m9 11 2 2 4-4" />
  </Svg>
);

export const IconPencil = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M12 20h9" />
    <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4 12.5-12.5Z" />
  </Svg>
);

export const IconDownload = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M12 4v10m0 0 4-4m-4 4-4-4" />
    <path d="M5 19h14" />
  </Svg>
);

export const IconRefresh = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M4 12a8 8 0 0 1 13.7-5.6L20 8" />
    <path d="M20 4v4h-4" />
    <path d="M20 12a8 8 0 0 1-13.7 5.6L4 16" />
    <path d="M4 20v-4h4" />
  </Svg>
);

export const IconCode = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="m9 8-4 4 4 4M15 8l4 4-4 4" />
  </Svg>
);

export const IconCopy = (p: GlyphProps) => (
  <Svg {...p}>
    <rect x="9" y="9" width="11" height="11" rx="2" />
    <path d="M5 15V5a2 2 0 0 1 2-2h8" />
  </Svg>
);

export const IconX = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M6 6l12 12M18 6 6 18" />
  </Svg>
);

export const IconTrash = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M4 7h16" />
    <path d="M9 7V5a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2" />
    <path d="M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" />
    <path d="M10 11v6M14 11v6" />
  </Svg>
);

export const IconSearch = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="11" cy="11" r="6" />
    <path d="m20 20-3.2-3.2" />
  </Svg>
);

export const IconFilter = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M4 5h16l-6 7v6l-4 2v-8z" />
  </Svg>
);

export const IconDots = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="5" cy="12" r="1.3" fill="currentColor" stroke="none" />
    <circle cx="12" cy="12" r="1.3" fill="currentColor" stroke="none" />
    <circle cx="19" cy="12" r="1.3" fill="currentColor" stroke="none" />
  </Svg>
);

export const IconClock = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="8" />
    <path d="M12 8v4l3 2" />
  </Svg>
);

export const IconCheckCircle = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="8" />
    <path d="m8.5 12 2.3 2.3L16 9" />
  </Svg>
);

export const IconAlertCircle = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="8" />
    <path d="M12 8v5M12 16.2v.01" />
  </Svg>
);

export const IconInfoCircle = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="8" />
    <path d="M12 11v5M12 7.8v.01" />
  </Svg>
);

export const IconUser = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="12" cy="8.5" r="3.5" />
    <path d="M5 19a7 7 0 0 1 14 0" />
  </Svg>
);

export const IconCloud = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M7 18a4 4 0 0 1-.5-7.97A5 5 0 0 1 16 9.5a3.5 3.5 0 0 1 1 6.9z" />
  </Svg>
);

export const IconBook = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M5 5a2 2 0 0 1 2-2h11v16H7a2 2 0 0 0-2 2z" />
    <path d="M18 17H7a2 2 0 0 0-2 2" />
  </Svg>
);

export const IconList = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M8 6h12M8 12h12M8 18h12" />
    <path d="M4 6v.01M4 12v.01M4 18v.01" />
  </Svg>
);

export const IconArrowLeft = (p: GlyphProps) => (
  <Svg {...p}>
    <path d="M20 12H5m0 0 6-6m-6 6 6 6" />
  </Svg>
);

export const IconCalendar = (p: GlyphProps) => (
  <Svg {...p}>
    <rect x="4" y="5" width="16" height="15" rx="2" />
    <path d="M4 9h16M8 3v4M16 3v4" />
  </Svg>
);

export const IconKey = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="8" cy="14" r="3.5" />
    <path d="m10.5 11.5 8-8M16 6l2 2M14 8l2 2" />
  </Svg>
);

export const IconTarget = (p: GlyphProps) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="8" />
    <circle cx="12" cy="12" r="3.5" />
  </Svg>
);

/* ---------------------------------------------------------------- */
/* Chef-hat accent — paired with section titles                     */
/* ---------------------------------------------------------------- */

export const ChefHatAccent = (p: GlyphProps) => (
  <Svg {...p} viewBox="0 0 32 32">
    <path d="M9 19a5 5 0 0 1-1.6-9.74A6 6 0 0 1 19 7.2 5 5 0 0 1 23 16.9V19z" />
    <path d="M9 19v5h14v-5" />
    <path d="M13 19v3M16 19v3M19 19v3" />
  </Svg>
);

/* ---------------------------------------------------------------- */
/* Decorative header art — mixing bowl + whisk + recipe book        */
/* Rendered top-right on Home and Recipes. Blue line art on the     */
/* light workspace.                                                 */
/* ---------------------------------------------------------------- */

export function KitchenHeaderArt({ className }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 240 140"
      fill="none"
      aria-hidden="true"
      focusable="false"
    >
      {/* soft pale-blue fills behind the line art */}
      <g fill="var(--c-blue)">
        <path d="M150 36h54a4 4 0 0 1 4 4v58a4 4 0 0 1-4 4h-54z" opacity="0.08" />
        <path d="M34 80h78l-7 24a14 14 0 0 1-13 10H54a14 14 0 0 1-13-10z" opacity="0.12" />
        <ellipse cx="73" cy="80" rx="45" ry="9" opacity="0.16" />
      </g>
      <g fill="var(--c-indigo)">
        <path d="M150 36a6 6 0 0 0-6 6v58a6 6 0 0 1 6-6z" opacity="0.10" />
      </g>
      {/* recipe book */}
      <g
        stroke="var(--c-blue)"
        strokeWidth={2.2}
        strokeLinecap="round"
        strokeLinejoin="round"
        opacity="0.9"
      >
        <path d="M150 36h54a4 4 0 0 1 4 4v58a4 4 0 0 1-4 4h-54z" />
        <path d="M150 36a6 6 0 0 0-6 6v58a6 6 0 0 1 6-6" />
        <path d="M150 42v56" />
        <path d="M162 54h34M162 66h34M162 78h24" />
      </g>
      {/* mixing bowl */}
      <g
        stroke="var(--c-blue)"
        strokeWidth={2.2}
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="M34 80h78l-7 24a14 14 0 0 1-13 10H54a14 14 0 0 1-13-10z" />
        <path d="M28 80h90" />
        <ellipse cx="73" cy="80" rx="45" ry="9" opacity="0.55" />
        <path d="M52 82c8 6 34 6 42 0" strokeWidth={1.6} opacity="0.5" />
      </g>
      {/* whisk */}
      <g
        stroke="var(--c-indigo)"
        strokeWidth={2.2}
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="M96 26 78 74" />
        <path d="M96 26c8 4 11 12 9 22M90 30c6 4 8 11 6 20M84 36c4 4 5 10 4 16" />
        <path d="m96 26 6-8" />
      </g>
      {/* sparkles */}
      <g stroke="var(--c-amber)" strokeWidth={2} strokeLinecap="round">
        <path d="M126 22v10M121 27h10" />
        <path d="M222 116v8M218 120h8" />
        <path d="M44 40v7M40.5 43.5h7" opacity="0.8" />
        <path d="M210 30v7M206.5 33.5h7" opacity="0.7" />
        <path d="M16 96v6M13 99h6" opacity="0.65" />
      </g>
    </svg>
  );
}

/* ---------------------------------------------------------------- */
/* Bottom-left nav illustration (also used as a fallback elsewhere) */
/* mixing bowl + whisk + measuring cup + sparkles                   */
/* ---------------------------------------------------------------- */

export function NavKitchenArt({ className }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 180 120"
      fill="none"
      aria-hidden="true"
      focusable="false"
    >
      <g
        stroke="var(--c-blue)"
        strokeWidth={2}
        strokeLinecap="round"
        strokeLinejoin="round"
        opacity="0.65"
      >
        {/* bowl */}
        <path d="M20 64h64l-6 20a12 12 0 0 1-11 8H37a12 12 0 0 1-11-8z" />
        <path d="M15 64h74" />
        {/* whisk */}
        <path d="M70 22 58 60" />
        <path d="M70 22c6 3 8 9 7 16M65 26c5 3 6 8 5 15" />
        {/* measuring cup */}
        <path d="M108 60h34v18a8 8 0 0 1-8 8h-18a8 8 0 0 1-8-8z" />
        <path d="M142 66h8a6 6 0 0 1 0 12h-8" />
        <path d="M116 66v14M124 66v14M132 66v14" opacity="0.7" />
      </g>
      <g stroke="var(--c-amber)" strokeWidth={1.8} strokeLinecap="round" opacity="0.8">
        <path d="M150 24v8M146 28h8" />
        <path d="M96 30v6M93 33h6" />
      </g>
    </svg>
  );
}
