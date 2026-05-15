import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { McpDto } from '../dtos/McpDto';

type McpState = {
  mcps: McpDto[];
  selectedMcps: McpDto[];
};

const initialState: McpState = {
  mcps: [],
  selectedMcps: [],
};

export const McpStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store) => ({
    /**
     * Sets the list of available MCP servers.
     *
     * @param mcps - Array of McpDto objects to set as available MCP servers
     */
    setMcps(mcps: McpDto[]): void {
      patchState(store, { mcps });
    },

    /**
     * Toggles the selection state of an MCP server.
     * If the MCP is already selected, it will be deselected, and vice versa.
     *
     * @param mcp - The McpDto to toggle selection for
     */
    toggleMcpSelection(mcp: McpDto): void {
      const current = store.selectedMcps();
      const index = current.findIndex((m) => m.name === mcp.name);
      const selectedMcps =
        index > -1
          ? current.filter((m) => m.name !== mcp.name)
          : [...current, mcp];
      patchState(store, { selectedMcps });
    },

    /**
     * Checks if a given MCP server is currently selected.
     *
     * @param mcp - The McpDto to check
     * @returns True if the MCP is selected, false otherwise
     */
    isMcpSelected(mcp: McpDto): boolean {
      return store.selectedMcps().some((m) => m.name === mcp.name);
    },
  })),
);
