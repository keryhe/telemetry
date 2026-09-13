import { AfterViewInit, Component, ElementRef, NgZone, OnDestroy, inject } from '@angular/core';

/**
 * Page header that stays put while the page body scrolls beneath it — status cards, the
 * filter/search bar, and (on detail pages) the breadcrumb.
 *
 * Deliberately not `position: sticky`: the page itself doesn't scroll. Per the layout contract in
 * styles.scss the sibling `.page-container` owns the scrolling, so this is simply a non-scrolling
 * flex item above it. That avoids every sticky-specific failure mode — content running past the
 * header's background, and nested `sticky: true` table headers racing it to the top of the shared
 * scrollport.
 *
 * The host spans the full content width so its background reads edge-to-edge like the shell
 * toolbar, while the inner wrapper carries the same max-width and padding as `.page-container`
 * to keep the header's contents aligned with the body column beneath it.
 */
@Component({
  selector: 'app-page-header',
  standalone: true,
  template: `
    <div class="page-header-inner">
      <ng-content></ng-content>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      flex: 0 0 auto;
      background: var(--mat-sys-surface);
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      transition: box-shadow 150ms ease;
    }

    /* Only once something is scrolled out of sight above the fold. */
    :host(.body-scrolled) {
      box-shadow: 0 2px 6px rgba(0, 0, 0, 0.08);
    }

    /* Below 959.98px this header scrolls away with the body instead of staying put (styles.scss),
       so there is no "content hidden above" to signal. Suppressed rather than left to the class,
       which can survive a resize down from a wider layout and strand a shadow on a header that
       now scrolls. */
    @media (max-width: 959.98px) {
      :host(.body-scrolled) {
        box-shadow: none;
      }
    }

    .page-header-inner {
      box-sizing: border-box;
      max-width: var(--page-max-width);
      margin: 0 auto;
      padding: 16px 16px 0;
    }
  `],
})
export class PageHeaderComponent implements AfterViewInit, OnDestroy {
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly zone = inject(NgZone);
  private scroller?: HTMLElement;

  private readonly onScroll = (): void => {
    const scrolled = (this.scroller?.scrollTop ?? 0) > 0;
    this.elementRef.nativeElement.classList.toggle('body-scrolled', scrolled);
  };

  ngAfterViewInit(): void {
    this.scroller = this.elementRef.nativeElement.parentElement
      ?.querySelector<HTMLElement>(':scope > .page-container') ?? undefined;

    // Outside Angular: this fires on every scrolled frame and only toggles a class, so running it
    // through change detection would re-render the page for nothing.
    this.zone.runOutsideAngular(() =>
      this.scroller?.addEventListener('scroll', this.onScroll, { passive: true }),
    );
  }

  ngOnDestroy(): void {
    this.scroller?.removeEventListener('scroll', this.onScroll);
  }
}
