import type { ChatMention } from "./types.ts";

/** Keep only selected, still-visible tokens. Typed @text never acquires an implicit ping. */
export function editMentions(before: string, after: string, mentions: ChatMention[]): ChatMention[] {
  let prefix = 0;
  while (prefix < before.length && prefix < after.length && before[prefix] === after[prefix]) prefix++;
  let suffix = 0;
  while (suffix < before.length - prefix && suffix < after.length - prefix && before[before.length - 1 - suffix] === after[after.length - 1 - suffix]) suffix++;
  const end = before.length - suffix, delta = after.length - before.length;
  return mentions.flatMap(item => item.start + item.length <= prefix ? [item] : item.start >= end ? [{ ...item, start: item.start + delta }] : []);
}
export function normalizedMentions(body: string, mentions: ChatMention[]): ChatMention[] {
  const leading = body.length - body.trimStart().length;
  const trimmed = body.trim();
  return mentions.filter(item => item.start >= leading && item.start + item.length <= leading + trimmed.length)
    .map(item => ({ ...item, start: body.slice(leading, item.start).replaceAll("\r\n", "\n").length }));
}
export function mentionQuery(body: string, caret: number): { start: number; search: string } | null {
  const match = /(?:^|\s)@([a-zA-Z0-9_]{0,30})$/.exec(body.slice(0, caret));
  return match ? { start: caret - match[1].length - 1, search: match[1] } : null;
}
