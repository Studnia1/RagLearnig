import unittest

from vinted_tracker.client import Listing
from vinted_tracker.deals import evaluate


def make_listing(price: float, title: str = "Gra testowa") -> Listing:
    return Listing(
        id=1, title=title, price=price, currency="PLN", total_price=None,
        brand=None, url="https://www.vinted.pl/items/1", photo_url=None, raw={},
    )


class TestEvaluate(unittest.TestCase):
    def test_below_max_price_is_deal(self):
        v = evaluate(make_listing(100), max_price=120, market_prices=[])
        self.assertTrue(v.is_deal)
        self.assertIn("próg", v.reasons[0])

    def test_above_max_price_no_market_is_not_deal(self):
        v = evaluate(make_listing(150), max_price=120, market_prices=[])
        self.assertFalse(v.is_deal)

    def test_far_below_median_is_deal(self):
        market = [200, 210, 190, 205, 195, 200]
        v = evaluate(make_listing(90), max_price=None, market_prices=market)
        self.assertTrue(v.is_deal)
        self.assertIn("mediany", v.reasons[0])

    def test_near_median_is_not_deal(self):
        market = [200, 210, 190, 205, 195, 200]
        v = evaluate(make_listing(180), max_price=None, market_prices=market)
        self.assertFalse(v.is_deal)

    def test_small_sample_median_ignored(self):
        v = evaluate(make_listing(90), max_price=None, market_prices=[200, 210])
        self.assertFalse(v.is_deal)

    def test_suspiciously_cheap_rejected(self):
        v = evaluate(make_listing(1.0), max_price=120, market_prices=[200] * 10)
        self.assertFalse(v.is_deal)

    def test_listing_from_api_price_variants(self):
        item = {
            "id": 42,
            "title": "Zelda TOTK",
            "price": {"amount": "149.0", "currency_code": "PLN"},
            "total_item_price": {"amount": "156.7", "currency_code": "PLN"},
            "brand_title": "Nintendo",
            "photo": {"url": "https://img"},
        }
        l = Listing.from_api(item, "https://www.vinted.pl")
        self.assertEqual(l.price, 149.0)
        self.assertEqual(l.currency, "PLN")
        self.assertEqual(l.total_price, 156.7)
        self.assertEqual(l.url, "https://www.vinted.pl/items/42")


if __name__ == "__main__":
    unittest.main()
