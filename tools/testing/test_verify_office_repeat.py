import hashlib
import json
import zipfile

import pytest

from verify_office_repeat import verify


def evidence(tmp_path, application="Word"):
    url = "https://office.example/shared/repeat.docx"
    cycles = []
    for i in (1, 2):
        package = tmp_path / f"cycle-{i}.zip"
        with zipfile.ZipFile(package, "w") as archive:
            part = "word/document.xml" if application == "Word" else "xl/sharedStrings.xml"
            archive.writestr(part, "<root>" + "".join(f"<t>marker-{n}</t>" for n in range(1, i + 1)) + "</root>")
            if application == "Word" and i == 2:
                archive.writestr("word/media/image.png", b"image")
        prefix = f"2026-10-06T12:0{i}:"
        cycles.append(dict(cycle=i, marker=f"marker-{i}", editKind="image" if application == "Word" and i == 2 else "text",
                           saveCalls=1, saved=True, reopened=True, remoteContentVerified=True,
                           saveStartedUtc=prefix + "00Z", saveReturnedUtc=prefix + "01Z",
                           saveVerifiedUtc=prefix + "03Z", reopenedUtc=prefix + "05Z",
                           etag=f"etag-{i}", packageFile=package.name,
                           sha256=hashlib.sha256(package.read_bytes()).hexdigest(),
                           expectedImages=i // 2 if application == "Word" else 0,
                           packageImages=i // 2 if application == "Word" else 0))
        capture = dict(schemaVersion=1, completedUtc=prefix + "02Z", files=[dict(url=url, errorCode="Success",
                       subResponses=[dict(errorCode="Success", partition="FileContents",
                       binary=dict(failed=False, operations=[dict(type="PutChanges", failed=False)]))])])
        (tmp_path / f"capture-{i}.summary.json").write_text(json.dumps(capture))
    result = dict(schemaVersion=1, application=application, candidateCommit="a" * 40, scriptSha256="b" * 64,
                  officeVersion="16", officeBuild="1234", origin="https://office.example", documentUrl=url,
                  expectedCycles=2, workerFinished=True, contentCheckPassed=True, cycles=cycles,
                  startedUtc="2026-10-06T12:00:00Z", completedUtc="2026-10-06T12:03:00Z")
    path = tmp_path / "repeat-result.json"
    path.write_text(json.dumps(result))
    return path, result


def test_content_alone_does_not_pass_manual_ui_gate(tmp_path):
    path, _ = evidence(tmp_path)
    report = verify(path, tmp_path, minimum_cycles=2)
    assert report["automatedContentPassed"]
    assert not report["passed"]
    assert "Manual" in report["errors"][0]


@pytest.mark.parametrize("application", ["Word", "Excel"])
def test_complete_evidence_passes(tmp_path, application):
    path, result = evidence(tmp_path, application)
    video = tmp_path / "observation.mp4"
    video.write_bytes(b"test fixture recording")
    manual = {key: result[key] for key in ("candidateCommit", "application", "origin", "officeBuild")}
    manual.update(observer="test fixture", cycles=[dict(cycle=i, singleSave=True, savedUi=True,
                  normalCloseWithoutPrompt=True, reopenedContent=True, imageEdit=i == 2,
                  observedUtc="2026-10-06T13:00:00Z", evidenceFile=video.name,
                  sha256=hashlib.sha256(video.read_bytes()).hexdigest()) for i in (1, 2)])
    manual_path = tmp_path / "manual.json"
    manual_path.write_text(json.dumps(manual))
    assert verify(path, tmp_path, manual_path, minimum_cycles=2)["passed"]


@pytest.mark.parametrize("key,value", [("saved", False), ("reopened", False), ("saveCalls", 2),
                                      ("packageImages", 0), ("editKind", "text"), ("sha256", "wrong")])
def test_rejects_bad_cycle(tmp_path, key, value):
    path, result = evidence(tmp_path)
    result["cycles"][1][key] = value
    path.write_text(json.dumps(result))
    assert not verify(path, tmp_path, minimum_cycles=2)["automatedContentPassed"]


def test_missing_cycle_and_release_threshold(tmp_path):
    path, result = evidence(tmp_path)
    assert not verify(path, tmp_path)["automatedContentPassed"]
    result["cycles"].pop()
    path.write_text(json.dumps(result))
    assert not verify(path, tmp_path, minimum_cycles=2)["automatedContentPassed"]


@pytest.mark.parametrize("change", ["missing", "outside-window", "wrong-origin", "malformed"])
def test_capture_must_match_each_cycle(tmp_path, change):
    path, _ = evidence(tmp_path)
    capture_path = tmp_path / "capture-2.summary.json"
    if change == "missing":
        capture_path.unlink()
    elif change == "malformed":
        capture_path.write_text("{")
    else:
        capture = json.loads(capture_path.read_text())
        if change == "outside-window":
            capture["completedUtc"] = "2026-10-06T12:01:02Z"
        else:
            capture["files"][0]["url"] = "https://other.example/shared/repeat.docx"
        capture_path.write_text(json.dumps(capture))
    assert not verify(path, tmp_path, minimum_cycles=2)["automatedContentPassed"]


@pytest.mark.parametrize("value", ["{", "[]", "null", '{"schemaVersion":1}'])
def test_malformed_result_fails_closed(tmp_path, value):
    path = tmp_path / "result.json"
    path.write_text(value)
    assert not verify(path, tmp_path)["passed"]


def test_package_tamper_fails(tmp_path):
    path, _ = evidence(tmp_path)
    (tmp_path / "cycle-2.zip").write_bytes(b"replaced package")
    assert not verify(path, tmp_path, minimum_cycles=2)["automatedContentPassed"]
