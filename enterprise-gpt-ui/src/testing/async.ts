/**
 * Lets pending promise chains run out. The session and token layers are promise-based, so a
 * request reaches `HttpTestingController` several microtasks late; a macrotask drains them all.
 */
export async function settle(passes = 3): Promise<void> {
  for (let pass = 0; pass < passes; pass++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}
