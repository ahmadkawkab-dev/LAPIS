/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: Object.fromEntries([
        'background', 'canvas', 'surface', 'foreground', 'muted-foreground',
        'primary', 'primary-foreground', 'secondary', 'selected', 'border',
        'control-border', 'focus', 'destructive', 'success', 'warning', 'graph-edge',
      ].map(name => [name, `var(--${name})`])),
      fontFamily: { sans: ['var(--font-ui)'], editorial: ['var(--font-editorial)'], mono: ['var(--font-mono)'] },
      borderRadius: { control: 'var(--radius-control)', card: 'var(--radius-card)', panel: 'var(--radius-panel)' },
      boxShadow: { card: 'var(--shadow-card)', panel: 'var(--shadow-panel)' },
      transitionDuration: { fast: 'var(--motion-fast)', panel: 'var(--motion-panel)', layout: 'var(--motion-layout)' },
      transitionTimingFunction: { settle: 'var(--ease-settle)', enter: 'var(--ease-enter)', exit: 'var(--ease-exit)' },
    },
  },
  plugins: [],
}
