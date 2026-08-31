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
    bankAccountId: null,
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

      // F1.e: el mismo effect refresca las cuentas bancarias, que alimentan el selector de la
      // Dropzone. Se piden con includeInactive para que el panel de la ficha de empresa pueda
      // reactivar una cuenta dada de baja.
      const bankReq = httpMock.expectOne(
        r => r.method === 'GET' && r.url.endsWith('/companies/company-1/bank-accounts'),
      );
      expect(bankReq.request.params.get('includeInactive')).toBe('true');
      bankReq.flush([]);
    });

    it('descarta el filtro por cuenta bancaria al cambiar de empresa', () => {
      // Las cuentas pertenecen a una empresa: mantener el filtro devolvería una grilla vacía sin
      // explicar por qué.
      service.setFilter({ bankAccountId: 'account-de-la-empresa-anterior' });

      companyService.activeCompany.set(makeCompany({ id: 'company-2' }));
      TestBed.tick();

      expect(service.filters().bankAccountId).toBeNull();

      const listReq = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/transactions'));
      expect(listReq.request.params.get('bankAccountId')).toBeNull();
      listReq.flush(pagedResult([]));

      httpMock.expectOne(r => r.url.endsWith('/companies/company-2/afip/vouchers')).flush([]);
      httpMock.expectOne(r => r.url.endsWith('/companies/company-2/bank-accounts')).flush([]);
    });

    // Regresión del "falso empty state": el usuario dejaba trabajo a medias en A, miraba B y al
    // volver a A el onboarding "Subí tu primer extracto" le tapaba todo. La causa era que cada
    // loadData() abría su propio subscribe: la respuesta —vacía— de B llegaba después de la de A
    // y pisaba la grilla. El listado en vuelo tiene que cancelarse al cambiar de empresa.
    it('cancela el listado de la empresa anterior al volver atrás (A → B → A)', () => {
      const flushSideRequests = (companyId: string) => {
        httpMock.expectOne(r => r.url.endsWith(`/companies/${companyId}/afip/vouchers`)).flush([]);
        httpMock.expectOne(r => r.url.endsWith(`/companies/${companyId}/bank-accounts`)).flush([]);
      };

      // Empresa A, con movimientos ya cargados.
      companyService.activeCompany.set(makeCompany({ id: 'company-a' }));
      TestBed.tick();
      flushList(pagedResult([makeTx({ id: 'tx-a' })], { totalCount: 1, totalPages: 1 }));
      flushSideRequests('company-a');
      expect(service.isEmptyCompany()).toBe(false);

      // Empresa B (vacía). Su listado queda en vuelo: el usuario vuelve antes de que responda.
      companyService.activeCompany.set(makeCompany({ id: 'company-b' }));
      TestBed.tick();
      flushSideRequests('company-b');

      // Vuelta a A.
      companyService.activeCompany.set(makeCompany({ id: 'company-a' }));
      TestBed.tick();
      flushSideRequests('company-a');

      const listRequests = httpMock.match(r => r.method === 'GET' && r.url.endsWith('/transactions'));
      const requestB = listRequests.find(r => r.request.params.get('companyId') === 'company-b');
      const liveRequests = listRequests.filter(r => !r.cancelled);

      // La de B ya no puede contestar, así que no hay forma de que pise la grilla de A.
      expect(requestB?.cancelled).toBe(true);
      expect(liveRequests.length).toBe(1);
      expect(liveRequests[0].request.params.get('companyId')).toBe('company-a');

      // Mientras la recarga está en vuelo tampoco se muestra el onboarding.
      expect(service.isEmptyCompany()).toBe(false);

      liveRequests[0].flush(pagedResult([makeTx({ id: 'tx-a' })], { totalCount: 1, totalPages: 1 }));

      expect(service.transactions().length).toBe(1);
      expect(service.isEmptyCompany()).toBe(false);
    });

    it('isEmptyCompany solo es true con la empresa activa cargada y sin movimientos', () => {
      companyService.activeCompany.set(makeCompany({ id: 'company-a' }));
      TestBed.tick();

      // En vuelo: todavía no se sabe si está vacía.
      expect(service.isEmptyCompany()).toBe(false);

      flushList(pagedResult([], { totalCount: 0, totalPages: 0 }));
      httpMock.expectOne(r => r.url.endsWith('/companies/company-a/afip/vouchers')).flush([]);
      httpMock.expectOne(r => r.url.endsWith('/companies/company-a/bank-accounts')).flush([]);

      expect(service.isEmptyCompany()).toBe(true);

      // Con un filtro puesto, la grilla vacía es un "sin resultados", no un onboarding.
      service.setFilter({ search: 'edenor' });
      expect(service.isEmptyCompany()).toBe(false);
    });

    // Regresión del "spinner infinito": tras reaplicar una regla, el GET de la grilla quedaba
    // colgado sin respuesta, sin error y sin cancelarse. isLoading se quedaba en true, la grilla
    // en skeleton y el overlay global bloqueaba la pantalla hasta recargar.
    it('apaga el skeleton si el listado nunca responde (timeout de 30 s)', () => {
      vi.useFakeTimers();
      try {
        const errorSpy = vi.spyOn(toast, 'error');

        companyService.activeCompany.set(makeCompany({ id: 'company-a' }));
        TestBed.tick();

        httpMock.expectOne(r => r.url.endsWith('/companies/company-a/afip/vouchers')).flush([]);
        httpMock.expectOne(r => r.url.endsWith('/companies/company-a/bank-accounts')).flush([]);

        const listReq = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/transactions'));
        expect(service.isLoading()).toBe(true);

        // La request no se responde nunca: solo el timeout puede sacar a la grilla de ahí.
        vi.advanceTimersByTime(30_000);

        expect(service.isLoading()).toBe(false);
        expect(listReq.cancelled).toBe(true);
        expect(errorSpy).toHaveBeenCalled();
      } finally {
        vi.useRealTimers();
      }
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

        // UX-2/UX-3: isGenerating sigue en true mientras el job corre (el botón "Asentar"
        // queda deshabilitado durante todo el polling para evitar el doble asentamiento).
        expect(service.isGenerating()).toBe(true);
        httpMock.expectNone(r => r.url.includes('/jobs/'));

        vi.advanceTimersByTime(3000);
        const status1 = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/jobs/job-1/status'));
        status1.flush({ jobId: 'job-1', state: 'Processing', createdAt: '2025-06-15T00:00:00Z' });
        expect(service.isGenerating()).toBe(true);

        vi.advanceTimersByTime(3000);
        const status2 = httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/jobs/job-1/status'));
        status2.flush({ jobId: 'job-1', state: 'Succeeded', createdAt: '2025-06-15T00:00:00Z' });

        // El job terminó: se libera el botón, se recarga la grilla y el polling se detiene.
        expect(service.isGenerating()).toBe(false);
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

        // takeWhile corta el polling tras el estado terminal; no hay recarga ni más requests,
        // y el botón se libera también en el caso de fallo.
        expect(service.isGenerating()).toBe(false);
        vi.advanceTimersByTime(10_000);
        httpMock.expectNone(r => r.url.includes('/jobs/'));
        httpMock.expectNone(r => r.method === 'GET' && r.url.endsWith('/transactions'));
      } finally {
        vi.useRealTimers();
      }
    });

    it('corta el polling con aviso si el job nunca termina (timeout de 5 minutos)', () => {
      vi.useFakeTimers();
      try {
        const warnSpy = vi.spyOn(toast, 'warning');
        seedEligibleTransaction();
        service.generateEntries([txId]);

        httpMock.expectOne(r => r.method === 'POST' && r.url.endsWith('/journal-entries/generate'))
          .flush({ jobId: 'job-3', message: 'Generación iniciada' });

        // El backend responde "Processing" en cada poll hasta agotar el tope (5 min / 3 s = 100).
        const maxPolls = Math.ceil((5 * 60_000) / 3000);
        for (let i = 0; i < maxPolls; i++) {
          vi.advanceTimersByTime(3000);
          httpMock.expectOne(r => r.method === 'GET' && r.url.endsWith('/jobs/job-3/status'))
            .flush({ jobId: 'job-3', state: 'Processing', createdAt: '2025-06-15T00:00:00Z' });
        }

        // Tope alcanzado: se libera el botón, se avisa al usuario y no hay más requests.
        expect(service.isGenerating()).toBe(false);
        expect(warnSpy).toHaveBeenCalledWith(
          'El proceso está tardando más de lo normal. Por favor, reintentá en unos minutos o contactá a soporte.'
        );
        vi.advanceTimersByTime(10_000);
        httpMock.expectNone(r => r.url.includes('/jobs/'));
      } finally {
        vi.useRealTimers();
      }
    });
  });
});
