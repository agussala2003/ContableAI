import { Currency } from '../../../core/services/transaction';

/** Opción del filtro por cuenta bancaria. `id` es un GUID, o 'none' para las sin cuenta. */
export interface BankAccountFilterOption {
  id: string;
  alias: string;
  currency: string;
}

export interface ReconciliationFilters {
  month: number | null;
  year: number | null;
  search: string;
  account: string;
  /** GUID, 'none' (movimientos sin cuenta) o null (todas). */
  bankAccountId: string | null;
  direction: 'debit' | 'credit' | null;
  currency: Currency | null;
  sortBy: string | null;
  sortDir: 'asc' | 'desc' | null;
  strictSearch: boolean;
  amountMode?: 'exact' | 'range';
  exactAmount?: number | null;
  minAmount?: number | null;
  maxAmount?: number | null;
}

export interface ReconciliationPagination {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}
