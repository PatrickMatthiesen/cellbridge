import json

from verify_office import accepted_saves


def capture(tmp_path, *, failed=False, partition="FileContents", outer="Success", file_error=None):
    record = {"schemaVersion": 1, "files": [{"url": "https://server/shared/unique.docx", "errorCode": file_error,
        "subResponses": [{"errorCode": outer, "partition": partition, "binary": {"failed": False,
            "operations": [{"type": "PutChanges", "failed": failed}]}}]}]}
    (tmp_path / "wire.summary.json").write_text(json.dumps(record))


def test_requires_actual_successful_file_put(tmp_path):
    capture(tmp_path)
    assert accepted_saves(tmp_path, "https://another-origin/shared/unique.docx") == ["wire.summary.json"]


def test_ignores_other_documents(tmp_path):
    capture(tmp_path)
    assert accepted_saves(tmp_path, "https://server/shared/other.docx") == []


def test_failed_put_does_not_pass(tmp_path):
    capture(tmp_path, failed=True)
    assert accepted_saves(tmp_path, "https://server/shared/unique.docx") == []


def test_metadata_partition_does_not_prove_file_save(tmp_path):
    capture(tmp_path, partition="EditorsTable")
    assert accepted_saves(tmp_path, "https://server/shared/unique.docx") == []


def test_soap_failure_does_not_pass(tmp_path):
    capture(tmp_path, outer="CellRequestFail")
    assert accepted_saves(tmp_path, "https://server/shared/unique.docx") == []


def test_file_failure_does_not_pass(tmp_path):
    capture(tmp_path, file_error="FileNotExistsOrCannotBeCreated")
    assert accepted_saves(tmp_path, "https://server/shared/unique.docx") == []


def test_missing_capture_is_not_evidence(tmp_path):
    assert accepted_saves(tmp_path, "https://server/shared/unique.docx") == []
