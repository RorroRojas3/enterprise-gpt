import { InjectionToken, Provider, Signal, Type } from '@angular/core';
import { ConversationDto } from '@domain/api/conversation';

/**
 * What the composer's project control is acting on (US-307).
 *
 * Two arms because the two screens differ in kind, not in degree: on the chat route the
 * project is a property of the open conversation and the reader may change it, while on
 * a project's detail screen the project *is* the route and there is nothing to pick.
 */
/** What the composer's download control acts on (US-1502). */
export interface ComposerExportTarget {
  readonly id: string;
  readonly name: string;
}

export type ComposerProjectTarget =
  | { readonly kind: 'conversation'; readonly conversation: ConversationDto }
  | { readonly kind: 'project'; readonly projectId: string };

/**
 * What the composer needs from whatever executes its prompt.
 *
 * The composer is one component on two screens that do different things with a
 * prompt: the chat route streams the turn in place (`TurnStore`), while the project
 * detail screen creates a conversation inside the project and hands the prompt to the
 * chat route (US-906). Naming that difference as a token is what lets the component
 * live in `shared/` at all — `shared → core` is the permitted direction, and
 * `shared → features/chat` is not.
 *
 * The surface is deliberately the members the composer actually reads, not
 * `TurnStore`'s whole API. A host that cannot stream simply reports `inFlight` for its
 * own request, seeds nothing, and has nothing to stop.
 */
export interface ComposerHost {
  /**
   * A one-shot text to place in the prompt box — a prompt chip, or a Retry restoring
   * what was typed. A fresh object per seed, so seeding the same text twice still
   * propagates; the composer nulls it back through {@link consumeComposerSeed}.
   */
  readonly composerSeed: Signal<{ readonly text: string } | null>;

  /** A request the host owns is running: the send control shows Stop. */
  readonly inFlight: Signal<boolean>;

  /**
   * Whether the last attempt settled into a notice some other surface owns.
   *
   * Typed as a bare object because the composer only ever asks whether there is one —
   * the notice's own shape belongs to whichever host produced it, and widening it here
   * would drag `features/chat` types into `core/`.
   */
  readonly turnError: Signal<object | null>;

  /**
   * What the project control acts on, or null when there is nothing to act on — empty
   * `/chat` before a conversation exists, and a deep link for the one round trip before
   * its row lands, which is the same rule the chat header's kebab already follows.
   *
   * The composer renders the control **absent** for a null rather than disabled, which
   * is this repo's pattern for an affordance with nothing behind it.
   */
  readonly projectTarget: Signal<ComposerProjectTarget | null>;

  /**
   * The conversation the download control offers, or null when there is nothing to
   * download — no conversation bound, or one whose transcript is still empty.
   *
   * Null renders the control **absent** rather than disabled, this repository's pattern
   * for an affordance with nothing behind it and US-1502's fifth criterion for an empty
   * conversation. A host that does not own a transcript reports null always.
   */
  readonly exportTarget: Signal<ComposerExportTarget | null>;

  consumeComposerSeed(): void;
  send(prompt: string): void;
  stop(): void;
}

export const COMPOSER_HOST = new InjectionToken<ComposerHost>('COMPOSER_HOST');

/**
 * Points the composer at the store that will execute its prompts.
 *
 * A function rather than a bare `{ provide, useExisting }` literal so the compiler
 * actually checks the store against {@link ComposerHost} — `useExisting` is typed
 * `any`, so a store that silently stopped satisfying the contract would fail at
 * runtime instead.
 *
 * @param host The store, already provided on the same injector.
 */
export function provideComposerHost(host: Type<ComposerHost>): Provider {
  return { provide: COMPOSER_HOST, useExisting: host };
}
