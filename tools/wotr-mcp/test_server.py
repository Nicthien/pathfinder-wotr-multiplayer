import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from server import _compare_states


class CompareStatesTests(unittest.TestCase):
    def test_equal_states_converge(self):
        state = {
            "gameId": "game",
            "stage": "Playing",
            "areaId": "area",
            "isPaused": False,
            "isInCombat": True,
            "combatMode": "RealTimeWithPause",
            "combatPhase": "Stable",
            "combatEpoch": 4,
            "combatHash": "1234ABCD",
            "pausePolicy": "AnyoneFreeResume",
            "pausedPlayers": [],
            "resumeVotes": [],
            "party": [{
                "id": "hero",
                "hp": 17,
                "maxHp": 20,
                "isInCombat": True,
                "position": {"x": 1.0, "y": 0.0, "z": 2.0},
            }],
        }

        result = _compare_states(state, state)

        self.assertTrue(result["converged"])
        self.assertEqual([], result["differences"])

    def test_hp_and_position_divergence_are_reported(self):
        host = {
            "party": [{"id": "hero", "hp": 17, "maxHp": 20, "isInCombat": True,
                       "position": {"x": 1.0, "y": 0.0, "z": 2.0}}]
        }
        client = {
            "party": [{"id": "hero", "hp": 9, "maxHp": 20, "isInCombat": True,
                       "position": {"x": 9.0, "y": 0.0, "z": 2.0}}]
        }

        result = _compare_states(host, client)

        self.assertFalse(result["converged"])
        fields = {difference["field"] for difference in result["differences"]}
        self.assertIn("party.hero.hp", fields)
        self.assertIn("party.hero.position", fields)

    def test_pause_policy_and_votes_divergence_are_reported(self):
        host = {
            "pausePolicy": "AnyoneFreeResume",
            "pausedPlayers": [-1],
            "resumeVotes": [],
        }
        client = {
            "pausePolicy": "AnyoneConsensusResume",
            "pausedPlayers": [],
            "resumeVotes": [42],
        }

        result = _compare_states(host, client)

        self.assertFalse(result["converged"])
        fields = {difference["field"] for difference in result["differences"]}
        self.assertEqual({"pausePolicy", "pausedPlayers", "resumeVotes"}, fields)


if __name__ == "__main__":
    unittest.main()
