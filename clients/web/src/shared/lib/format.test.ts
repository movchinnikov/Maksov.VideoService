import { describe, expect, it } from 'vitest';
import { describeProblem } from '../api/client';
import { failureMessage, fileSize, timecode, titleFromFileName } from './format';

describe('timecode', () => {
  it('formats minutes and seconds', () => expect(timecode(83_456)).toBe('01:23'));
  it('formats hours', () => expect(timecode(3_723_000)).toBe('1:02:03'));
  it('shows placeholder for unknown', () => expect(timecode(null)).toBe('--:--'));
});

describe('fileSize', () => {
  it('uses binary units', () => expect(fileSize(5 * 1024 * 1024)).toBe('5.0 MB'));
  it('handles null', () => expect(fileSize(null)).toBe('—'));
});

describe('titleFromFileName', () => {
  it('drops extension and underscores', () => expect(titleFromFileName('Sunset_over_sea.final.mp4')).toBe('Sunset over sea.final'));
});

describe('failureMessage', () => {
  it('maps known codes', () => expect(failureMessage('duration_exceeded')).toContain('10 minutes'));
  it('falls back for unknown', () => expect(failureMessage('weird')).toBe('Processing failed.'));
});

describe('describeProblem', () => {
  it('prefers field validation message', () =>
    expect(describeProblem(400, { errors: { sizeBytes: ['File is larger than 2048 MB.'] } })).toBe('File is larger than 2048 MB.'));
  it('hides internals of 500', () => expect(describeProblem(500, { title: 'NullReferenceException' })).toContain('server'));
  it('explains 404 without leaking existence', () => expect(describeProblem(404, null)).toContain('not yours'));
});

