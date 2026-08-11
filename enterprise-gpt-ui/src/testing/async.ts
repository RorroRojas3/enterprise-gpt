/**
 * Lets pending promise chains run to completion.
 *
 * The session and token layers are promise-based rather than observable-based — a
 * guard has to `await` its work — so a request does not reach `HttpTestingController`
 * in the same microtask it was issued in, and a single `await Promise.resolve()`
 * drains only one link of the chain. A macrotask boundary drains all of them, which
 * keeps specs free of hop counting.
 */
export async function settle(passes = 3): Promise<void> {
  for (let pass = 0; pass < passes; pass++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}
