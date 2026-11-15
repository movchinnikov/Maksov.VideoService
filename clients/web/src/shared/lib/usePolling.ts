import { useEffect, useRef } from 'react';

/**
 * Polling with a growing delay (1 → 2 → 5 s) while condition() is true.
 * Stops on unmount and when the tab is hidden — saves network and battery.
 */
export function usePolling(tick: (signal: AbortSignal) => Promise<boolean>, enabled: boolean) {
  const tickRef = useRef(tick);
  useEffect(() => {
    tickRef.current = tick;
  });

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    const delays = [1000, 2000, 5000];
    let attempt = 0;
    let timer: number | undefined;

    const schedule = () => {
      const delay = delays[Math.min(attempt, delays.length - 1)] ?? 5000;
      timer = window.setTimeout(run, delay);
    };

    const run = async () => {
      if (document.hidden) {
        schedule();
        return;
      }
      attempt++;
      try {
        const keepGoing = await tickRef.current(controller.signal);
        if (keepGoing && !controller.signal.aborted) schedule();
      } catch {
        if (!controller.signal.aborted) schedule();
      }
    };

    schedule();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [enabled]);
}

