// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {ERC721} from "@openzeppelin/contracts/token/ERC721/ERC721.sol";

// Same shape as 0G's ERC-7857 metadata (IERC7857Metadata): each token carries a list of intelligent data
// entries, each a description plus the 0G Storage root hash of encrypted data.
struct IntelligentData {
    string dataDescription;
    bytes32 dataHash;
}

/// @title Iron Corner robot boxer as a 0G Agentic ID (ERC-7857 style).
/// The data entries point at the bot's encrypted brain and its latest training session on 0G Storage.
/// Every training session the game server ("trainer") records a new brain version here, so the bot's
/// growth is a public, ordered history. Transfers are plain ERC-721: the TEE re-encryption of the brain
/// on transfer (iTransferFrom in the full standard) is not implemented in this hackathon build.
contract IronCornerAgent is ERC721 {
    address public immutable trainer;
    uint256 public nextId = 1;

    mapping(uint256 => IntelligentData[]) private _datas;
    mapping(uint256 => uint256) public brainVersion;
    mapping(uint256 => uint16) public level;

    event Updated(uint256 indexed _tokenId, IntelligentData[] _oldDatas, IntelligentData[] _newDatas);
    event Trained(uint256 indexed tokenId, uint256 version, uint16 level, bytes32 brainRoot);

    modifier onlyTrainer() {
        require(msg.sender == trainer, "only trainer");
        _;
    }

    constructor(address _trainer) ERC721("Iron Corner Robot", "IRONBOT") {
        trainer = _trainer;
    }

    function mint(address to, IntelligentData[] calldata datas, uint256 version, uint16 lvl)
        external onlyTrainer returns (uint256 id)
    {
        id = nextId++;
        _safeMint(to, id);
        _set(id, datas, version, lvl);
    }

    function recordTraining(uint256 id, IntelligentData[] calldata datas, uint256 version, uint16 lvl) external onlyTrainer {
        _requireOwned(id);
        require(version > brainVersion[id], "version must grow");
        _set(id, datas, version, lvl);
    }

    function intelligentDatasOf(uint256 id) external view returns (IntelligentData[] memory) {
        _requireOwned(id);
        return _datas[id];
    }

    function _set(uint256 id, IntelligentData[] calldata datas, uint256 version, uint16 lvl) private {
        require(datas.length > 0 && datas.length <= 4, "1-4 data entries");
        IntelligentData[] memory old = _datas[id];
        delete _datas[id];
        for (uint256 i = 0; i < datas.length; i++) _datas[id].push(datas[i]);
        brainVersion[id] = version;
        level[id] = lvl;
        emit Updated(id, old, datas);
        emit Trained(id, version, lvl, datas[0].dataHash);
    }
}
