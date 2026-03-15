export interface ReconciliationFilters {
  month:   number | null;
  year:    number | null;
  search:  string;
  account: string;
  sortBy:  string | null;
  sortDir: 'asc' | 'desc' | null;
}

export interface ReconciliationPagination {
  page:       number;
  pageSize:   number;
  totalCount: number;
  totalPages: number;
}
