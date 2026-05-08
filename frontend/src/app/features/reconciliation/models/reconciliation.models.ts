export interface ReconciliationFilters {
  month: number | null;
  year: number | null;
  search: string;
  account: string;
  direction: 'debit' | 'credit' | null;
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
