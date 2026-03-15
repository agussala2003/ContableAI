export type Direction = 'DEBIT' | 'CREDIT' | null;

export type RuleFilterType = 'all' | 'own' | 'global';

export interface RuleForm {
  keyword: string;
  targetAccount: string;
  direction: Direction;
  priority: number;
  requiresTaxMatching: boolean;
}
