import { InjectionToken, Provider, Signal, Type } from '@angular/core';
import { ConversationDto } from '@shared/models/api/conversation.model';

/** What the composer's download control acts on. */
export interface ComposerExportTarget {
  readonly id: string;
  readonly name: string;
}

/**
 * What the composer's project control acts on: the open conversation's project, which the reader
 * may change, or the project a detail screen is routed to, where there is nothing to pick.
 */
export type ComposerProjectTarget =
  | { readonly kind: 'conversation'; readonly conversation: ConversationDto }
  | { readonly kind: 'project'; readonly projectId: string };

/**
 * What the composer needs from whatever executes its prompt. A token because two pages supply
 * different hosts: chat's `TurnStore` and the project screen's `ProjectComposerHost`.
 */
export interface ComposerHost {
  /** A fresh object per seed, so seeding the same text twice still propagates. */
  readonly composerSeed: Signal<{ readonly text: string } | null>;

  readonly inFlight: Signal<boolean>;

  /** Typed as a bare object so the chat components' notice types stay out of `core/`. */
  readonly turnError: Signal<object | null>;

  /** Null hides the control rather than disabling it, e.g. on empty `/chat`. */
  readonly projectTarget: Signal<ComposerProjectTarget | null>;

  /** Null (no conversation, or an empty transcript) hides the control rather than disabling it. */
  readonly exportTarget: Signal<ComposerExportTarget | null>;

  consumeComposerSeed(): void;
  send(prompt: string): void;
  stop(): void;
}

export const COMPOSER_HOST = new InjectionToken<ComposerHost>('COMPOSER_HOST');

/**
 * A function rather than a `useExisting` literal, which is typed `any`, so the compiler checks
 * the store against {@link ComposerHost}.
 */
export function provideComposerHost(host: Type<ComposerHost>): Provider {
  return { provide: COMPOSER_HOST, useExisting: host };
}
