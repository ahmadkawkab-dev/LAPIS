import { useEffect, useState } from 'react';

/** Reserve content geometry immediately; reveal placeholders only for a noticeable wait. */
export function LoadingSkeleton({ label, layout = 'list' }: {
  label: string;
  layout?: 'list' | 'cards' | 'week' | 'calendar' | 'dashboard' | 'chat' | 'form';
}) {
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const timer = window.setTimeout(() => setVisible(true), 180);
    return () => window.clearTimeout(timer);
  }, []);
  const count = layout === 'calendar' ? 35 : layout === 'week' ? 7 : layout === 'cards' ? 6 : 4;
  return <div className={`wk-loading wk-loading--${layout}`} role="status" aria-label={label} aria-busy="true">
    <div className={`wk-loading-shapes${visible ? ' is-visible' : ''}`} aria-hidden="true">
      {Array.from({ length: count }, (_, index) => <div className="wk-loading-item" key={index}>
        <span className="wk-loading-mark" /><div><span className="wk-skeleton-line wk-skeleton-line--title" />
          <span className="wk-skeleton-line" /><span className="wk-skeleton-line wk-skeleton-line--short" /></div>
      </div>)}
    </div>
  </div>;
}
