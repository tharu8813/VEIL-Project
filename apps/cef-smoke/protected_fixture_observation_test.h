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
  std::cout << "PASS: synthetic fixed fixture load/title/closure evidence and refusal\n";
  return 0;
}
