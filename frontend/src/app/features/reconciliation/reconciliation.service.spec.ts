import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReconciliationService } from './reconciliation.service';
import { CompanyService, Company } from '../../core/services/company.service';
import { ToastService } from '../../core/services/toast.service';
import { BankTransaction, PagedResult } from '../../core/services/transaction';

// ── Fixtures ──────────────────────────────────────────────────────────────

function makeCompany(overrides: Partial<Company> = {}): Company {
  return {
    id: 'company-default',
    name: 'Empresa Test',
    cuit: '30-00000000-1',
    businessType: 'GENERAL',
    isActive: true,
    splitChequeTax: false,
    bankAccountName: 'Banco Test',
    ...overrides,
  };
}

function makeTx(overrides: Partial<BankTransaction> = {}): BankTransaction {
  return {
    id: 'tx-default',
    date: '2025-06-15',
    description: 'Movimiento test',
    externalId: null,
    amount: 100,
    currency: 'ARS',
    type: 0,
    assignedAccount: '',
    needsTaxMatching: false,
    classificationSource: 'Pending',
    confidenceScore: 0,
    needsBreakdown: false,
    isPossibleDuplicate: false,
    tenantId: 'tenant',
    companyId: null,
    journalEntryId: null,
    ...overrides,
  };
}

function pagedResult(
  items: BankTransaction[],
  overrides: Partial<PagedResult<BankTransaction>> = {},
): PagedResult<BankTransaction> {
  return {
    items,
    totalCount: items.length,
    page: 1,
    pageSize: 10,
    totalPages: items.length > 0 ? 1 : 0,
    totalIngresosFiltered: 0,
    totalEgresosFiltered: 0,
    totalIngresosAll: 0,
    totalEgresosAll: 0,
    ...overrides,
  };
}

describe('ReconciliationService', () => {
  let service: ReconciliationService;
  let httpMock: HttpTestingController;
  let companyService: CompanyService;
  let toast: ToastService;

  /** El único GET a la lista de transacciones pendiente, resuelto con el resultado dado. */
  const flushList = (result: PagedResult<BankTransaction>) => {
    const req = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/transactions'));
    req.flush(result);
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), ReconciliationService],
    });

    httpMock = TestBed.inject(HttpTestingController);
    companyService = TestBed.inject(CompanyService);
    toast = TestBed.inject(ToastService);
    service = TestBed.inject(ReconciliationService);

    // El constructor registra un effect que dispara loadData()+refreshAfipCount() en la
    // primera ejecución. Sin empresa activa, refreshAfipCount no genera HTTP (solo loadData).
    TestBed.tick();
    flushList(pagedResult([]));
  });

  afterEach(() => {
    // Falla si quedó alguna request sin resolver — evita fugas de aserciones entre tests.
    httpMock.verify();
  });

  // ── Manejo del estado: filtros y actualización de listas ─────────────────

  describe('estado y filtros', () => {
    it('setFilter actualiza el signal de filtros sin disparar una recarga', () => {
      service.setFilter({ search: 'edenor' });
      expect(service.filters().search).toBe('edenor');
      // Sin request pendiente: httpMock.verify() en afterEach lo detectaría.
    });

    it('hasActiveFilters refleja si hay algún filtro activo', () => {
      expect(service.hasActiveFilters()).toBe(false);
      service.setFilter({ search: 'edenor' });
      expect(service.hasActiveFilters()).toBe(true);
      service.setFilter({ search: '' });
      expect(service.hasActiveFilters()).toBe(false);
    });

    it('applyFilters resetea a página 1, recarga y actualiza transacciones/paginación/totales', () => {
      service.setFilter({ search: 'edenor' });
      service.applyFilters();
      expect(service.isLoading()).toBe(true);

      const tx = makeTx({ id: 't1', description: 'PAGO EDENOR', assignedAccount: 'Servicios Públicos' });
      flushList(pagedResult([tx], {
        totalCount: 1,
        totalPages: 1,
        totalEgresosFiltered: 100,
        availableAccounts: ['Servicios Públicos'],
      }));

      expect(service.transactions()).toEqual([tx]);
      expect(service.pagination()).toEqual({ page: 1, pageSize: 10, totalCount: 1, totalPages: 1 });
      expect(service.totalEgresos()).toBe(100);
      expect(service.availableAccounts()).toEqual(['Servicios Públicos']);
      expect(service.isLoading()).toBe(false);
    });

    it('clearFilters limpia los filtros y recarga la grilla', () => {
      service.setFilter({ search: 'edenor', account: 'Caja', direction: 'debit' });
      service.applyFilters();
      flushList(pagedResult([]));

      service.clearFilters();
      expect(service.filters().search).toBe('');
      expect(service.filters().account).toBe('');
      expect(service.filters().direction).toBeNull();
      expect(service.hasActiveFilters()).toBe(false);

      flushList(pagedResult([]));
    });

    it('changePage actualiza la página de inmediato y recarga', () => {
      service.changePage(2);
      expect(service.pagination().page).toBe(2);
      expect(service.isLoading()).toBe(true);

      const req = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/transactions'));
      expect(req.request.params.get('page')).toBe('2');
      req.flush(pagedResult([], { page: 2 }));
    });

    it('setPageSize acota el valor a [1,500] y vuelve a página 1', () => {
      service.setPageSize(1000);
      expect(service.pagination().pageSize).toBe(500);
      expect(service.pagination().page).toBe(1);
      flushList(pagedResult([]));
    });

    it('recarga los datos (y el conteo AFIP) cuando cambia la empresa activa', () => {
      companyService.activeCompany.set(makeCompany({ id: 'company-1' }));
      TestBed.tick(); // flush del effect que reacciona al cambio de activeCompany

      expect(service.isLoading()).toBe(true);
      const listReq = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/transactions'));
      expect(listReq.request.params.get('companyId')).toBe('company-1');
      listReq.flush(pagedResult([]));

      const afipReq = httpMock.expectOne(
        r => r.method === 'GET' && r.url.endsWith('/companies/company-1/afip/vouchers'),
      );
      afipReq.flush([]);
      expect(service.pendingAfipCount()).toBe(0);
    });
  });

  // ── Pila de deshacer (undo stack) ─────────────────────────────────────────

  describe('undo stack', () => {
    const txId = 'tx-1';

    function seedTransaction(assignedAccount: string): void {
      service.applyFilters();
      flushList(pagedResult([makeTx({ id: txId, assignedAccount })]));
    }

    const flushAccountUpdate = (id: string, resultingAccount: string) => {
      const req = httpMock.expectOne(r => r.method === 'PUT' && r.url.endsWith(`/transactions/${id}`));
      req.flush({
        transaction: makeTx({ id, assignedAccount: resultingAccount }),
        newSuggestionKeyword: null,
      });
      return req;
    };

    it('updateTransaction aplica el cambio de forma optimista y lo confirma con el backend', () => {
      seedTransaction('Cuenta A');

      service.updateTransaction(txId, 'Cuenta B');
      // Optimista: el cambio se ve antes de que la request resuelva.
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta B');

      const req = flushAccountUpdate(txId, 'Cuenta B');
      expect(req.request.body).toEqual({ assignedAccount: 'Cuenta B' });
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta B');
    });

    it('undoLastUpdate revierte el estado al valor anterior al deshacer', () => {
      seedTransaction('Cuenta A');

      service.updateTransaction(txId, 'Cuenta B');
      flushAccountUpdate(txId, 'Cuenta B');
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta B');

      service.undoLastUpdate();
      // Optimista: vuelve a "Cuenta A" antes de que la request de deshacer resuelva.
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta A');

      const undoReq = httpMock.expectOne(r => r.method === 'PUT' && r.url.endsWith(`/transactions/${txId}`));
      expect(undoReq.request.body).toEqual({ assignedAccount: 'Cuenta A' });
      undoReq.flush({ transaction: makeTx({ id: txId, assignedAccount: 'Cuenta A' }), newSuggestionKeyword: null });

      expect(service.transactions()[0].assignedAccount).toBe('Cuenta A');
    });

    it('undoLastUpdate sobre una pila vacía avisa y no genera ninguna request', () => {
      const warnSpy = vi.spyOn(toast, 'warning');

      service.undoLastUpdate();

      expect(warnSpy).toHaveBeenCalledWith('No hay acciones recientes para deshacer en esta vista.');
      // httpMock.verify() en afterEach confirma que no quedó ninguna request pendiente.
    });

    it('si falla la actualización, revierte el cambio optimista y descarta la entrada del undo', () => {
      seedTransaction('Cuenta A');
      service.updateTransaction(txId, 'Cuenta B');
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta B');

      const req = httpMock.expectOne(r => r.method === 'PUT' && r.url.endsWith(`/transactions/${txId}`));
      req.flush({ message: 'boom' }, { status: 500, statusText: 'Server Error' });

      // Rollback al snapshot previo al intento optimista.
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta A');

      // El undo se descartó junto con el rollback: no queda nada para deshacer.
      const warnSpy = vi.spyOn(toast, 'warning');
      service.undoLastUpdate();
      expect(warnSpy).toHaveBeenCalledWith('No hay acciones recientes para deshacer en esta vista.');
    });

    it('si falla el deshacer, restaura el estado deshecho y reencola la entrada del undo', () => {
      seedTransaction('Cuenta A');
      service.updateTransaction(txId, 'Cuenta B');
      flushAccountUpdate(txId, 'Cuenta B');

      service.undoLastUpdate();
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta A');

      const undoReq = httpMock.expectOne(r => r.method === 'PUT' && r.url.endsWith(`/transactions/${txId}`));
      undoReq.flush({ message: 'boom' }, { status: 500, statusText: 'Server Error' });

      // Rollback del deshacer: vuelve a "Cuenta B" (estado previo al intento de undo).
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta B');

      // La entrada se reencoló: un segundo undo debe volver a intentar revertir a "Cuenta A".
      service.undoLastUpdate();
      expect(service.transactions()[0].assignedAccount).toBe('Cuenta A');
      flushAccountUpdate(txId, 'Cuenta A');
    });
  });

  // ── Polling del estado del job de generación de asientos ──────────────────

  describe('generación de asientos y polling', () => {
    const txId = 'tx-eligible';

    function seedEligibleTransaction(): void {
      service.applyFilters();
      flushList(pagedResult([makeTx({ id: txId, assignedAccount: 'Proveedores', journalEntryId: null })]));
    }

    it('sondea el estado del job hasta que termina, y recarga la grilla al finalizar con éxito', () => {
      vi.useFakeTimers();
      try {
        seedEligibleTransaction();

        service.generateEntries([txId]);
        expect(service.isGenerating()).toBe(true);

        const genReq = httpMock.expectOne(r => r.method === 'POST' && r.url.endsWith('/journal-entries/generate'));
        expect(genReq.request.body).toEqual({ transactionIds: [txId] });
        genReq.flush({ jobId: 'job-1', message: 'Generación iniciada' });

        // isGenerating se apaga apenas se encola el job; el polling sigue en background.
        expect(service.isGenerating()).toBe(false);
        httpMock.expectNone(r => r.url.includes('/jobs/'));

        vi.advanceTimersByTime(3000);
        const status1 = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/jobs/job-1/status'));
        status1.flush({ jobId: 'job-1', state: 'Processing', createdAt: '2025-06-15T00:00:00Z' });

        vi.advanceTimersByTime(3000);
        const status2 = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/jobs/job-1/status'));
        status2.flush({ jobId: 'job-1', state: 'Succeeded', createdAt: '2025-06-15T00:00:00Z' });

        // El job terminó: se recarga la grilla y el polling se detiene (takeWhile completa).
        flushList(pagedResult([]));
        vi.advanceTimersByTime(10_000);
        httpMock.expectNone(r => r.url.includes('/jobs/'));
      } finally {
        vi.useRealTimers();
      }
    });

    it('detiene el polling sin recargar si el job termina en Failed', () => {
      vi.useFakeTimers();
      try {
        seedEligibleTransaction();
        service.generateEntries([txId]);

        httpMock.expectOne(r => r.method === 'POST' && r.url.endsWith('/journal-entries/generate'))
          .flush({ jobId: 'job-2', message: 'Generación iniciada' });

        vi.advanceTimersByTime(3000);
        httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/jobs/job-2/status'))
          .flush({ jobId: 'job-2', state: 'Failed', createdAt: '2025-06-15T00:00:00Z' });

        // takeWhile corta el polling tras el estado terminal; no hay recarga ni más requests.
        vi.advanceTimersByTime(10_000);
        httpMock.expectNone(r => r.url.includes('/jobs/'));
        httpMock.expectNone(r => r.method === 'GET' && r.url.endsWith('/transactions'));
      } finally {
        vi.useRealTimers();
      }
    });
  });
});
