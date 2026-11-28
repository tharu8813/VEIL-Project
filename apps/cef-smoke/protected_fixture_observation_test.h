// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_fixture_observation.h"
#include <iostream>

inline int TestProtectedFixtureObservation() {
  using veil_cef::ProtectedFixtureObservation;
  ProtectedFixtureObservation good;
  good.Created(1); good.Title(1, true, true); good.Load(1, true);
  if (!good.Ready() || good.Result().passed || good.Result().browsers_closed) return 1;
  good.RequestClose(1); good.Closed(1);
  if (!good.Result().passed || !good.Result().browsers_closed) return 2;
  ProtectedFixtureObservation wrong_page;
  wrong_page.Created(1); wrong_page.Load(1, false); wrong_page.Title(1, true, true);
  wrong_page.RequestClose(1); wrong_page.Closed(1);
  if (wrong_page.Result().passed || !wrong_page.Result().browsers_closed) return 3;
  ProtectedFixtureObservation early;
  early.Created(1); early.Closed(1);
  if (early.Result().passed || !early.Result().browsers_closed) return 4;
  ProtectedFixtureObservation extra;
  extra.Created(1); extra.Created(2); extra.Closed(1); extra.Closed(2);
  if (extra.Result().passed || extra.Result().browsers_closed) return 5;
  ProtectedFixtureObservation wrong_close;
  wrong_close.Created(1); wrong_close.Load(1, true); wrong_close.Title(1, true, true);
  wrong_close.RequestClose(1); wrong_close.Closed(2);
  if (wrong_close.Result().passed || wrong_close.Result().browsers_closed) return 6;
  ProtectedFixtureObservation revoked;
  revoked.Created(1); revoked.Load(1, true); revoked.Title(1, true, true);
  revoked.Fail(); revoked.RequestClose(1); revoked.Closed(1);
  if (revoked.Result().passed || !revoked.Result().browsers_closed) return 7;
  ProtectedFixtureObservation missing;
  missing.Created(1); missing.Load(1, true); missing.RequestClose(1); missing.Closed(1);
  if (missing.Result().passed || !missing.Result().browsers_closed) return 8;
  ProtectedFixtureObservation duplicate;
  duplicate.Created(1); duplicate.Load(1, true); duplicate.Title(1, true, true);
  duplicate.RequestClose(1); duplicate.Closed(1); duplicate.Closed(1);
  if (duplicate.Result().passed || duplicate.Result().browsers_closed) return 9;
  ProtectedFixtureObservation changed_title;
  changed_title.Created(1); changed_title.Load(1, true); changed_title.Title(1, true, true);
  changed_title.Title(1, true, false); changed_title.Title(1, true, true);
  changed_title.RequestClose(1); changed_title.Closed(1);
  if (changed_title.Result().passed || !changed_title.Result().browsers_closed) return 10;
  ProtectedFixtureObservation changed_during_close;
  changed_during_close.Created(1); changed_during_close.Load(1, true);
  changed_during_close.Title(1, true, true); changed_during_close.RequestClose(1);
  changed_during_close.Title(1, false, false); changed_during_close.Closed(1);
  if (changed_during_close.Result().passed || !changed_during_close.Result().browsers_closed) return 11;
  ProtectedFixtureObservation unknown_load;
  unknown_load.Created(1); unknown_load.Load(1, true); unknown_load.Title(1, true, true);
  unknown_load.RequestClose(1); unknown_load.Load(2, true); unknown_load.Closed(1);
  if (unknown_load.Result().passed || unknown_load.Result().browsers_closed) return 12;
  ProtectedFixtureObservation unknown_title;
  unknown_title.Created(1); unknown_title.Title(2, false, false);
  unknown_title.Load(1, true); unknown_title.Title(1, true, true); unknown_title.Closed(1);
  if (unknown_title.Result().passed || unknown_title.Result().browsers_closed) return 13;
  ProtectedFixtureObservation initial_title;
  initial_title.Created(1); initial_title.Title(1, false, false);
  initial_title.Load(1, true); initial_title.Title(1, true, true);
  initial_title.RequestClose(1); initial_title.Closed(1);
  if (!initial_title.Result().passed || !initial_title.Result().browsers_closed) return 14;
  ProtectedFixtureObservation premature_callback;
  premature_callback.Load(1, true); premature_callback.Created(1);
  premature_callback.Title(1, true, true); premature_callback.Closed(1);
  if (premature_callback.Result().passed || premature_callback.Result().browsers_closed) return 15;
  std::cout << "PASS: synthetic fixed fixture load/title/closure evidence and refusal\n";
  return 0;
}
