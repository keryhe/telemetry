import { formatDuration, formatUnitLabel, formatUnitValue, parseUnit } from './chart.utils';

describe('parseUnit', () => {
  it('treats a missing, empty, dimensionless or braced-only unit as "none"', () => {
    for (const u of [undefined, null, '', '   ', '1', '{requests}', '{request}', '{connection}']) {
      const p = parseUnit(u);
      expect(p.kind).withContext(String(u)).toBe('none');
      expect(p.perSecond).withContext(String(u)).toBeFalse();
    }
  });

  it('strips an annotation attached to a unit', () => {
    expect(parseUnit('By{sent}').kind).toBe('bytesBinary');
    expect(parseUnit('{request}/s')).toEqual(jasmine.objectContaining({ kind: 'none', perSecond: true }));
  });

  it('matches the unambiguous spellings case-insensitively', () => {
    for (const u of ['By', 'by', 'BY', 'bytes', 'Bytes']) expect(parseUnit(u).kind).withContext(u).toBe('bytesBinary');
    for (const u of ['ms', 'MS', 'Ms', 'milliseconds']) expect(parseUnit(u).factor).withContext(u).toBe(1);
    expect(parseUnit('seconds').factor).toBe(1000);
    expect(parseUnit('hz').kind).toBe('hz');
  });

  it('does not guess for an ambiguous prefixed or single-letter spelling', () => {
    // UCUM is case-sensitive: `mBy` is millibytes, not MBy. `S` is siemens, `H` henry.
    for (const u of ['mBy', 'Mby', 'kiby', 'S', 'H', 'D']) expect(parseUnit(u).kind).withContext(u).toBe('unknown');
  });

  it('keeps the spelling of an unrecognised unit, annotations removed', () => {
    expect(parseUnit('req')).toEqual(jasmine.objectContaining({ kind: 'unknown', text: 'req' }));
    expect(parseUnit('req{x}/s')).toEqual(jasmine.objectContaining({ kind: 'unknown', perSecond: true, text: 'req/s' }));
  });

  it('distinguishes decimal from binary bytes', () => {
    expect(parseUnit('KiBy')).toEqual(jasmine.objectContaining({ kind: 'bytesBinary', factor: 1024 }));
    expect(parseUnit('KBy')).toEqual(jasmine.objectContaining({ kind: 'bytesDecimal', factor: 1000 }));
    expect(parseUnit('MBy').factor).toBe(1_000_000);
  });
});

describe('formatUnitLabel', () => {
  it('is empty when there is nothing to show', () => {
    for (const u of [undefined, null, '', '{requests}', '{connection}', '1']) expect(formatUnitLabel(u)).withContext(String(u)).toBe('');
  });
  it('returns the unit spelling otherwise', () => {
    expect(formatUnitLabel('By')).toBe('By');
    expect(formatUnitLabel('By{sent}')).toBe('By');
    expect(formatUnitLabel('req')).toBe('req');
    expect(formatUnitLabel('{request}/s')).toBe('/s');
    expect(formatUnitLabel('%')).toBe('%');
  });
});

describe('formatUnitValue', () => {
  describe('time', () => {
    it('scales through us, ms and s, and stays capped at seconds for a unit of seconds', () => {
      expect(formatUnitValue(0.0005, 's')).toBe('500µs');
      expect(formatUnitValue(0.25, 's')).toBe('250.0ms');
      expect(formatUnitValue(2.5, 's')).toBe('2.50s');
      expect(formatUnitValue(7200, 's')).toBe('7200.00s');
      expect(formatUnitValue(1500, 'ms')).toBe('1.50s');
      expect(formatUnitValue(2_000_000, 'ns')).toBe('2.0ms');
    });
    it('keeps scaling above seconds when the unit is min, h or d', () => {
      expect(formatUnitValue(90, 'min')).toBe('1.5h');
      expect(formatUnitValue(0.5, 'min')).toBe('30.00s');
      expect(formatUnitValue(48, 'h')).toBe('2.0d');
      expect(formatUnitValue(3, 'd')).toBe('3.0d');
    });
    it('handles a negative value', () => {
      expect(formatUnitValue(-2.5, 's')).toBe('-2.50s');
    });
  });

  describe('bytes', () => {
    it('labels binary steps KiB/MiB/GiB at the 1024 boundary', () => {
      expect(formatUnitValue(1023, 'By')).toBe('1023B');
      expect(formatUnitValue(1024, 'By')).toBe('1.00KiB');
      expect(formatUnitValue(1024 ** 3, 'By')).toBe('1.00GiB');
      expect(formatUnitValue(512, 'MiBy')).toBe('512.00MiB');
      expect(formatUnitValue(1024 ** 2, 'KiBy')).toBe('1.00GiB');
    });
    it('is not case sensitive about By', () => {
      expect(formatUnitValue(2048, 'by')).toBe('2.00KiB');
      expect(formatUnitValue(2048, 'bytes')).toBe('2.00KiB');
    });
    it('labels decimal steps kB/MB/GB at the 1000 boundary', () => {
      expect(formatUnitValue(999, 'By')).toBe('999B');
      expect(formatUnitValue(999, 'KBy')).toBe('999.00kB');
      expect(formatUnitValue(1000, 'KBy')).toBe('1.00MB');
      expect(formatUnitValue(1.5, 'GBy')).toBe('1.50GB');
    });
    it('the same number reads differently in the two families', () => {
      expect(formatUnitValue(1, 'KiBy')).toBe('1.00KiB');
      expect(formatUnitValue(1, 'KBy')).toBe('1.00kB');
      expect(formatUnitValue(1024, 'KBy')).toBe('1.02MB');
    });
    it('keeps a per-second rate', () => {
      expect(formatUnitValue(2048, 'By/s')).toBe('2.00KiB/s');
      expect(formatUnitValue(2048, 'By', { perSecond: true })).toBe('2.00KiB/s');
      // a unit that is already a rate is not turned into a rate of a rate
      expect(formatUnitValue(2048, 'By/s', { perSecond: true })).toBe('2.00KiB/s');
    });
  });

  describe('other families', () => {
    it('formats bits, Hz and Cel', () => {
      expect(formatUnitValue(12, 'bit')).toBe('12 bit');
      expect(formatUnitValue(1500, 'bit')).toBe('1.50 kbit');
      expect(formatUnitValue(3.2, 'Hz')).toBe('3.2 Hz');
      expect(formatUnitValue(41.5, 'Cel')).toBe('41.5 °C');
    });
    it('keeps % and the dimensionless 1', () => {
      expect(formatUnitValue(42.5, '%')).toBe('42.5%');
      expect(formatUnitValue(0.5, '1')).toBe('0.5');
    });
  });

  describe('braced and unknown units', () => {
    it('shows only the number for a braced unit', () => {
      expect(formatUnitValue(7, '{requests}')).toBe('7');
      expect(formatUnitValue(0.5, '{request}')).toBe('0.5');
      expect(formatUnitValue(7, '{requests}', { perSecond: true })).toBe('7/s');
      expect(formatUnitValue(7, '{request}/s')).toBe('7/s');
    });
    it('appends an unrecognised unit as written', () => {
      expect(formatUnitValue(7, 'req')).toBe('7 req');
      expect(formatUnitValue(7, 'req', { perSecond: true })).toBe('7 req/s');
      expect(formatUnitValue(7, 'req/s')).toBe('7 req/s');
    });
    it('has no unit text for a missing unit', () => {
      expect(formatUnitValue(7, undefined)).toBe('7');
      expect(formatUnitValue(7, '', { perSecond: true })).toBe('7/s');
    });
  });

  describe('plain numbers', () => {
    it('keeps every digit of a four- or five-figure count', () => {
      expect(formatUnitValue(12345, '{requests}')).toBe((12345).toLocaleString());
      expect(formatUnitValue(1234.6, '')).toBe((1235).toLocaleString());
    });
    it('keeps precision for typical and tiny magnitudes', () => {
      expect(formatUnitValue(0, '')).toBe('0');
      expect(formatUnitValue(12.3456, '')).toBe('12.346');
      expect(formatUnitValue(0.00123456, '')).toBe('0.00123');
    });
  });
});

describe('formatDuration (unchanged, used by traces)', () => {
  it('still scales µs/ms/s and caps at seconds', () => {
    expect(formatDuration(0.5)).toBe('500µs');
    expect(formatDuration(12.34)).toBe('12.3ms');
    expect(formatDuration(120_000)).toBe('120.00s');
  });
});
