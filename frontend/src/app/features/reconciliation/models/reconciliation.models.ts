import { Currency } from '../../../core/services/transaction';

/** Opción del filtro por cuenta bancaria. `id` es un GUID, o 'none' para las sin cuenta. */
export interface BankAccountFilterOption {
  id: string;
  alias: string;
  currency: string;
  /** Banco de la cuenta. Alimenta la cascada Banco → Cuenta sin pedir datos de nuevo. */
  bankCode: string | null;
}

/** Opción del filtro por banco. `code` es un código del catálogo, o 'none' para lo sin banco. */
export interface BankFilterOption {
  code: string;
  label: string;
}

export interface ReconciliationFilters {
  month: number | null;
  year: number | null;
  search: string;
  account: string;
  /** Código de banco, 'none' (lo que no se puede atribuir a un banco) o null (todos). */
  bankCode: string | null;
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
