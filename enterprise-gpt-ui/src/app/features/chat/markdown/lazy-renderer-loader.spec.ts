import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideTestAppConfig } from '@testing/app-config';
import { DIAGRAM_MODULE, LazyRendererLoader, MATH_MODULE } from './lazy-renderer-loader';

/** Mermaid cannot lay out under jsdom (no `getBBox` or `document.fonts`), so its thunk is faked. */
describe('LazyRendererLoader', () => {
  let loadDiagram: ReturnType<typeof vi.fn>;
  let loadMath: ReturnType<typeof vi.fn>;
  let mermaid: {
    initialize: ReturnType<typeof vi.fn>;
    parse: ReturnType<typeof vi.fn>;
    render: ReturnType<typeof vi.fn>;
  };
  let renderToString: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    mermaid = {
      initialize: vi.fn(),
      parse: vi.fn().mockResolvedValue({ diagramType: 'flowchart-v2' }),
      render: vi.fn().mockResolvedValue({ svg: '<svg></svg>' }),
    };
    renderToString = vi.fn().mockReturnValue('<span class="katex"></span>');
    loadDiagram = vi.fn().mockResolvedValue(mermaid);
    loadMath = vi.fn().mockResolvedValue({ renderToString });
  });

  afterEach(() => {
    // The stylesheet goes on the real `document.head`, which outlives the TestBed.
    for (const link of katexLinks()) {
      link.remove();
    }
  });

  function katexLinks(): HTMLLinkElement[] {
    return [...document.head.querySelectorAll<HTMLLinkElement>('link[rel="stylesheet"]')].filter(
      (link) => link.getAttribute('href')?.includes('katex'),
    );
  }

  function loader(features: { diagrams: boolean; math: boolean }): LazyRendererLoader {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig({ features: { ...features, rawStreamCodec: false } }),
        { provide: DIAGRAM_MODULE, useValue: loadDiagram },
        { provide: MATH_MODULE, useValue: loadMath },
        LazyRendererLoader,
      ],
    });

    return TestBed.inject(LazyRendererLoader);
  }

  describe('the flags', () => {
    it('never reaches the import when diagrams are off', async () => {
      // A flag checked after the import would render nothing but still download a megabyte.
      expect(await loader({ diagrams: false, math: false }).getDiagramRenderer()).toBeNull();
      expect(loadDiagram).not.toHaveBeenCalled();
    });

    it('never reaches the import when math is off', async () => {
      expect(await loader({ diagrams: false, math: false }).getMathRenderer()).toBeNull();
      expect(loadMath).not.toHaveBeenCalled();
    });

    it('loads once for however many diagrams an answer holds', async () => {
      const service = loader({ diagrams: true, math: false });

      await Promise.all([
        service.getDiagramRenderer(),
        service.getDiagramRenderer(),
        service.getDiagramRenderer(),
      ]);

      expect(loadDiagram).toHaveBeenCalledOnce();
    });

    it('degrades to null when the chunk cannot be fetched, and retries later', async () => {
      loadDiagram.mockRejectedValueOnce(new Error('offline'));
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      const service = loader({ diagrams: true, math: false });

      // Resolves rather than rejects: callers are fire-and-forget.
      expect(await service.getDiagramRenderer()).toBeNull();
      expect(await service.getDiagramRenderer()).not.toBeNull();
      expect(loadDiagram).toHaveBeenCalledTimes(2);
    });
  });

  describe('the diagram renderer', () => {
    it('parses before it renders, and never renders a source that failed', async () => {
      mermaid.parse.mockResolvedValue(false);
      const renderer = await loader({ diagrams: true, math: false }).getDiagramRenderer();

      await expect(renderer?.render('not a diagram')).rejects.toThrow();
      // Mermaid's error graphic would replace the source on the page.
      expect(mermaid.render).not.toHaveBeenCalled();
    });

    it('renders under a configuration that cannot execute anything', async () => {
      const renderer = await loader({ diagrams: true, math: false }).getDiagramRenderer();
      await renderer?.render('graph TD; A-->B');

      const config = mermaid.initialize.mock.calls[0]?.[0] as Record<string, unknown>;
      expect(config['startOnLoad']).toBe(false);
      expect(config['securityLevel']).toBe('strict');
      expect(config['htmlLabels']).toBe(false);
      expect(config['suppressErrorRendering']).toBe(true);
      expect(config['maxTextSize']).toBe(20_000);
      expect(config['maxEdges']).toBe(200);
    });

    it('gives every diagram on the page its own id', async () => {
      const renderer = await loader({ diagrams: true, math: false }).getDiagramRenderer();
      await renderer?.render('graph TD; A-->B');
      await renderer?.render('graph TD; C-->D');

      // Mermaid's temporary measuring element takes this id, so a shared one would collide.
      const [first, second] = mermaid.render.mock.calls.map(([id]) => id as string);
      expect(first).not.toBe(second);
    });

    it('drops theme variables it cannot resolve rather than passing them empty', async () => {
      // jsdom loads no stylesheet, so every token reads back as ''.
      const renderer = await loader({ diagrams: true, math: false }).getDiagramRenderer();
      await renderer?.render('graph TD; A-->B');

      const config = mermaid.initialize.mock.calls[0]?.[0] as { themeVariables: object };
      expect(Object.values(config.themeVariables)).not.toContain('');
    });
  });

  describe('the math renderer', () => {
    it('renders against options that cannot reach outside the page', async () => {
      const renderer = await loader({ diagrams: false, math: true }).getMathRenderer();
      renderer?.render('x^2', false);

      const [source, options] = renderToString.mock.calls[0] as [string, Record<string, unknown>];
      expect(source).toBe('x^2');
      // `trust` enables \href, \url and \includegraphics.
      expect(options['trust']).toBe(false);
      // A bad expression renders as its own source rather than aborting the others.
      expect(options['throwOnError']).toBe(false);
      expect(options['maxExpand']).toBe(100);
    });

    it('carries the display flag through to KaTeX', async () => {
      const renderer = await loader({ diagrams: false, math: true }).getMathRenderer();
      renderer?.render('E = mc^2', true);

      const options = renderToString.mock.calls[0]?.[1] as { displayMode: boolean };
      expect(options.displayMode).toBe(true);
    });

    it('omits errorColor rather than passing it empty', async () => {
      // jsdom resolves no tokens, so `--bs-danger` reads back as '', which KaTeX takes literally.
      const renderer = await loader({ diagrams: false, math: true }).getMathRenderer();
      renderer?.render('x^2', false);

      const options = renderToString.mock.calls[0]?.[1] as Record<string, unknown>;
      expect(options['errorColor']).toBeUndefined();
    });

    it('links the self-hosted stylesheet once, from this origin', async () => {
      const service = loader({ diagrams: false, math: true });
      await service.getMathRenderer();
      await service.getMathRenderer();

      const links = katexLinks();
      expect(links).toHaveLength(1);
      // Copied out of node_modules by `copy-katex.mjs`, never a CDN.
      expect(links[0]?.getAttribute('href')).toContain('vendor/katex/katex.min.css');
    });
  });
});
