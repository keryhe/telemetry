import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { APP_CONFIG } from '../../config/app-config';
import { TracesApiService } from './traces-api.service';

describe('TracesApiService.getSpans', () => {
  let service: TracesApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: APP_CONFIG, useValue: { apiUrl: '/api' } },
      ],
    });
    service = TestBed.inject(TracesApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const span = (id: string, resourceIndex: number, scopeIndex: number) => ({
    traceIdHex: 't', spanIdHex: id, name: id, kind: 2, startTimeUnixNano: 1, endTimeUnixNano: 2,
    statusCode: 1, flags: 0, droppedAttributesCount: 0, droppedEventsCount: 0, droppedLinksCount: 0,
    events: [], links: [], resourceIndex, scopeIndex,
  });

  it('gives every span its own resource and scope, from the lists the response carries once', () => {
    let result: any[] = [];
    service.getSpans('t').subscribe((spans) => (result = spans));

    http.expectOne((r) => r.url.endsWith('/t/spans')).flush({
      resources: [{ attributes: { 'service.name': 'a' } }, { attributes: { 'service.name': 'b' } }],
      scopes: [{ name: 'lib' }],
      spans: [span('1', 0, 0), span('2', 1, 0), span('3', 0, 0)],
    });

    expect(result.map((s) => s.resource.attributes['service.name'])).toEqual(['a', 'b', 'a']);
    expect(result.every((s) => s.instrumentationScope.name === 'lib')).toBeTrue();
    // The same object for spans of one resource, as the server sends it once.
    expect(result[0].resource).toBe(result[2].resource);
    // The index fields are transport detail and do not leak into the model.
    expect('resourceIndex' in result[0]).toBeFalse();
  });

  it('sends the time hint only when there is a whole one (start and end)', () => {
    service.getSpans('t', '2026-03-01T00:00:00Z', '2026-03-01T00:00:01Z').subscribe();
    const hinted = http.expectOne((r) => r.url.endsWith('/t/spans'));
    expect(hinted.request.params.get('start')).toBe('2026-03-01T00:00:00Z');
    expect(hinted.request.params.get('end')).toBe('2026-03-01T00:00:01Z');
    hinted.flush({ resources: [], scopes: [], spans: [] });

    // Half a hint is no hint: the server would ignore it anyway.
    service.getSpans('t', '2026-03-01T00:00:00Z').subscribe();
    const half = http.expectOne((r) => r.url.endsWith('/t/spans'));
    expect(half.request.params.has('start')).toBeFalse();
    half.flush({ resources: [], scopes: [], spans: [] });

    service.getSpans('t').subscribe();
    const plain = http.expectOne((r) => r.url.endsWith('/t/spans'));
    expect(plain.request.params.has('start')).toBeFalse();
    plain.flush({ resources: [], scopes: [], spans: [] });
  });
});
