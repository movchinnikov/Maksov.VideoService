import { describe, expect, it } from 'vitest';
import { describeState, formatTags, isVertical, shouldPollDescription } from './description';

describe('describeState', () => {
  it('waits while video is not ready', () => {
    expect(describeState(null, 'processing')).toBe('wait');
    expect(describeState(null, 'uploaded')).toBe('wait');
  });
  it('is none for failed video', () => expect(describeState(null, 'failed')).toBe('none'));
  it('works on ready video without description yet', () => expect(describeState(null, 'ready')).toBe('working'));
  it('works while queued/processing', () => {
    expect(describeState('queued', 'ready')).toBe('working');
    expect(describeState('processing', 'ready')).toBe('working');
  });
  it('passes terminal statuses', () => {
    expect(describeState('done', 'ready')).toBe('done');
    expect(describeState('failed', 'ready')).toBe('failed');
  });
});

describe('shouldPollDescription', () => {
  it('polls only non-terminal', () => {
    expect(shouldPollDescription('wait')).toBe(true);
    expect(shouldPollDescription('working')).toBe(true);
    expect(shouldPollDescription('done')).toBe(false);
    expect(shouldPollDescription('failed')).toBe(false);
    expect(shouldPollDescription('none')).toBe(false);
  });
});

describe('isVertical', () => {
  it('9:16 is vertical', () => expect(isVertical(1080, 1920)).toBe(true));
  it('16:9 is not', () => expect(isVertical(1920, 1080)).toBe(false));
  it('square is vertical', () => expect(isVertical(720, 720)).toBe(true));
  it('unknown defaults to vertical', () => expect(isVertical(null, null)).toBe(true));
});

describe('formatTags', () => {
  it('joins with middle dot', () => expect(formatTags(['a', ' b ', ''])).toBe('a · b'));
});
